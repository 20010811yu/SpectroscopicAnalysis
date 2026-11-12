# WorkflowOrchestrator 流程编排设计（需求）

> 依据：[上位机任务清单.md](上位机任务清单.md) 第四节（流程编排 P3–P6）、第七节（状态机 P6）、第九节（安全与模式 P8/P33）、第十节（并发控制）、第二节（通信接口）；[开发规范.md](开发规范.md) 3.1/3.2、第 6/7 节、11.1/11.3；[SctStrategy引擎设计.md](SctStrategy引擎设计.md) §6/§10。
> 范围：**生产流程编排、SCT 任务执行编排、锁/预检/超时/急停、设备接口挂点**（抽象定义，不实现 Modbus/GNR 协议细节）。

## 1. 背景与定位

`WorkflowOrchestrator` 是上位机的执行中枢：**流程状态机 + 设备编排**（开发规范 3.1）。策略引擎（SctStrategyEngine，见引擎设计文档）只产出"该做什么"的任务，Workflow 负责"把它做完"：

```
UI → Workflow（本文） → SctStrategy 引擎 / SampleManager / Correction / Vision → DeviceInterface（设备抽象） / 数据层
```

- Workflow 从引擎队列出队任务（`TryDequeue`），按状态机推进步骤，经 DeviceInterface 抽象驱动 PLC/光谱仪，完成后 `CompleteTask` 回执并把结果事件回喂引擎；
- Workflow 持有资源互斥锁（RobotLock / SparkStandLock / SampleArchiveLock——引擎设计 §6.2 明确归 Workflow）；
- 依赖方向遵守开发规范 3.2：禁止反向依赖与跨层直调，模块间经接口通信，接口定义在被依赖方；
- 纯编排逻辑可测：设备访问全部经接口，测试注入 Fake 网关（tests/ 现有 Fake 仓储模式扩展）。

## 2. 职责边界

| 职责 | 说明 |
|---|---|
| 状态机驱动 | 维护任务状态机与 SCT 子状态机，约束合法转移（不允许跳跃，开发规范 11.3） |
| 生产流程编排 | 九步流程逐步推进（§5） |
| SCT 任务执行 | 从引擎出队 → 下发 → 回执（§6） |
| 预检与锁 | 下发前 SafetyOK/RobotReady/SparkStandReady 预检；资源互斥锁/模式锁管理（§7） |
| 超时与重试 | 所有设备调用带超时与重试上限（集中配置），耗尽走报警链路（§7.3） |
| 急停恢复 | Emergency 事件处理、任务取消、故障复位恢复（§7.4） |
| 轨迹审计 | 每步起止时间、触发原因、人工干预写轨迹日志（任务清单第 12 节） |

**不做**：触发判断/选样（引擎）、系数计算（Correction 模块）、视觉选点算法（Vision 模块）、Modbus 寄存器/GNR 协议实现（DeviceInterface）、UI 更新（经事件上抛）。

## 3. 输入与依赖

| 依赖 | 来源 | 用途 |
|---|---|---|
| `ISctStrategyEngine` | SctStrategy（引擎设计 §10 草案） | TryDequeue/CompleteTask/各 Notify 事件回喂 |
| `IDeviceGateway` 设备抽象族 | DeviceInterface（§8 定义） | PLC 任务下发/状态/安全信号、光谱仪激发/读结果、心跳 |
| `ISctSampleRepository` 等 | 数据层（已有） | 样品档案、执行记录、轨迹日志 |
| Correction 计算服务 | Correction 模块 | 生产样第 7 步浓度修正（接口挂点） |
| `IErrorCollector` + `ErrorCodes` | LoggingAlarm（已有） | 统一错误链 |
| `WorkflowOptions`（新增，集中配置） | appsettings.json | 各步超时、重试上限、轮询周期、心跳间隔——禁止散落硬编码（开发规范 6/11.3） |

## 4. 核心模型：三类状态机（任务清单第 7 节原文，命名不得自创）

- **系统模式**：`Manual / Auto / Maintenance / Emergency`
- **任务状态**：`Idle → Registered → Preparing → Milling → Placing → Analyzing → Archiving → Completed / Failed`
- **SCT 子状态**：`Selecting → Retrieving → MeasuringHeight → Milling → Analyzing → UpdatingCoeff → Returning`

转移规则：仅允许沿箭头顺序转移；任何状态可转入 `Failed`（带错误码）；`Emergency` 模式下 Running 任务全部终止。转移表以常量字典硬编码，启动时自检（测试穷举校验无非法转移入口）。

## 5. 生产流程九步编排（任务清单 63–73 行）

| 步 | 内容 | 前置条件 | 设备调用/模块 | 状态转移 | 失败分支 |
|---|---|---|---|---|---|
| 1 | 样品注册（本机或外部系统） | 档案校验通过 | 样品档案写库（事务） | Idle→Registered | 5003 入库校验失败 |
| 2 | 接收进样到位通知 | 已注册 | SAMPLE_POSITIONED 事件/轮询 | Registered→Preparing | 超时→等待重试 |
| 3 | 下发抓取任务（含抓取高度） | 预检通过（§7.2）+ RobotLock | ISendTask（ID_CYCLE=1） | Preparing→Milling | 1001 抓取失败→报警暂停 |
| 4 | 视觉选点坐标（可选） | Vision 服务可用 | Vision 模块接口 | —（附加信息） | 1002 未检测到样品→走默认位 |
| 5 | **SCT 插入判断** | — | 引擎 `TryDequeue` | 有 SCT 任务 → 挂起生产，转 §6 | — |
| 6 | 触发 GNR 激发并读取结果 | SparkStandLock + SparkStandReady | ISpectrometer（协议 100–140） | Analyzing | 2001 下压异常/通信失败→重试→报警 |
| 7 | 系数修正与合格判定 | — | Correction 计算（CoefficientLock 读锁） | — | 超差→按策略/报警 |
| 8 | 归档管理 | 分析完成 | SampleArchiveLock；标签打印指令；档案与轨迹更新 | Archiving→Completed | 5004 写库失败→事务回滚 |
| 9 | 循环下一件 | — | 回到第 2 步；同时 `NotifyAnalysisCompleted` 回喂引擎 | →Idle | — |

驱动方式：async 步骤链 + 状态机守卫；每步完成后持久化当前状态（进程重启可判断断点，人工恢复）。

## 6. SCT 任务执行编排

```
Workflow 主循环（Auto 模式）:
  engine.TryDequeue(out task)
    → 按 SctTaskType 映射 CYCLE_START_REQUEST:
        Standardize  → ID_CYCLE=2（设置样）
        ControlCheck → ID_CYCLE=3（控制样）
        Production   → ID_CYCLE=1（生产分析）
    → SCT 子状态机推进:
        Selecting（引擎已选样，记录 SampleId/Reason）
        → Retrieving（机器人抓取，RobotLock）
        → MeasuringHeight（仅 HeightMeasure 任务：不下激发，
            ISendTask.MeasureHeight → 结果回 engine.NotifyHeightMeasured）
        → Milling / Placing（同生产 3–4 步）
        → Analyzing（激发，ISpectrometer）
        → UpdatingCoeff（标准化任务：Correction 写新系数版本，CoefficientLock 写锁；
            完成回 engine.NotifyStandardizeCompleted）
        → Returning（归位，释放锁）
    → engine.CompleteTask(taskId, success, error)
    → 结果事件回喂: NotifyAnalysisCompleted / NotifyControlResult（判定结果）
```

- ControlCheck 的合格判定结果（按策略配置 LimitType/LimitValue）由 Workflow 计算后经 `NotifyControlResult` 回喂引擎，超差闭环推进由引擎负责（引擎设计 §7）；
- HeightMeasure 任务回填的厚度流转（NeedMeasure→Normal/Disabled）仍走现有测高登记同一套规则（`SctViewModel` §测高登记状态流），自动路径只是数据来源不同；
- SCT 任务运行期间生产任务保持队列（暂停生产分析，P19）。

## 7. 锁、预检、超时/重试、急停

### 7.1 资源互斥锁（任务清单 119/121 行）

| 锁 | 保护资源 | 主要持有步骤 | 释放时机 |
|---|---|---|---|
| RobotLock | 机器人 | 抓取/归位（Retrieving→Returning） | 动作完成应答 |
| SparkStandLock | 激发台 | 激发（Analyzing） | 读结果完成 |
| CoefficientLock（读写锁） | 系数表 | 生产读取=读锁共享；标准化写入=写锁独占 | 计算/写库完成 |
| SampleArchiveLock | 归档区 | 第 8 步归档 | 放置指令应答 |
| 模式锁 | 手动 vs 自动 | 手动操作期间拒绝自动任务入执行 | 模式切换确认 |

锁实现为进程内 `SemaphoreSlim` 封装（单机上位机，无需分布式）；带超时获取，获取失败→任务排队等待，不强制抢占。

### 7.2 下发前预检（开发规范 11.3/193 行、任务清单 113 行）

每次向 PLC 下发任务前必须全部通过：

1. `SafetyOK`（安全门关闭，3001 反例）
2. `RobotReady`
3. `SparkStandReady`
4. **样品位置一致性校验**：传感器/视觉反馈位置 == 软件记录位置，不一致 → **拒绝切入/执行自动任务**（安全规则，不可配置跳过）

任一不通过 → 本次下发取消，任务回队或标 Failed，写审计日志。

### 7.3 超时与重试（开发规范 11.3/192 行、第 6 节）

- 所有设备通信调用带超时 + 重试上限，值取 `WorkflowOptions` 集中配置（appsettings.json，如 `Workflow:Steps:Grip:TimeoutMs`）；
- 重试耗尽 → 按错误码走报警链路（IErrorCollector），任务 Failed，**不自动无限重试**；
- 心跳：1–2 秒递增计数寄存器（IHeartbeat），中断超阈值 → 全部任务挂起，进入安全状态（开发规范第 6 节）；
- 状态轮询周期同样集中配置（IGetTaskStatus）。

### 7.4 急停与恢复（任务清单 111 行、9001）

```
Emergency 事件（急停按钮/PLC 信号/通信中断升级）
  → 立即停止下发，Running 任务终止（状态记录到断点）
  → engine.EmergencyReset()（清队列，全部 Cancelled——引擎设计 §10）
  → 系统模式 → Emergency；安全门锁定逻辑交 PLC
恢复：
  人工排除故障 → 故障复位操作（必须人工，不可自动恢复）
  → 预检（§7.2）全部通过 → 模式回 Manual/Auto → 断点任务人工确认后续（重跑/放弃）
```

## 8. 设备接口挂点（DeviceInterface 抽象，开发规范 3.2/76 行）

接口定义在 DeviceInterface 模块，Workflow 只依赖接口；Modbus TCP 为当前实现，OPC UA 可替换，**Modbus 细节（寄存器地址/报文）不外泄到接口之外**：

```csharp
/// <summary>PLC 任务下发（命令握手：命令寄存器写值 + 应答读值）。</summary>
public interface ISendTask
{
    /// <summary>下发循环任务；idCycle: 1 生产 / 2 标准化 / 3 控制样（CYCLE_START_REQUEST）。</summary>
    Task SendCycleStartAsync(int idCycle, string sampleId, double gripHeight, CancellationToken ct);
    Task MeasureHeightAsync(string sampleId, CancellationToken ct);   // 测高指令
}

/// <summary>PLC 状态轮询（轮询 + 变化检测）。</summary>
public interface IGetTaskStatus
{
    Task<DeviceTaskStatus> GetTaskStatusAsync(CancellationToken ct);
}

/// <summary>安全信号预检（SafetyOK / RobotReady / SparkStandReady / 样品位置）。</summary>
public interface IGetSafetyStatus
{
    Task<SafetyStatus> GetSafetyStatusAsync(CancellationToken ct);
}

/// <summary>光谱仪（TCP，协议号 100–140）。</summary>
public interface ISpectrometer
{
    Task TriggerSparkAsync(string sampleId, CancellationToken ct);    // 触发激发
    Task<SparkResult> ReadResultAsync(CancellationToken ct);          // 读取原始强度
}

/// <summary>心跳维护（递增计数寄存器，1–2 秒）。</summary>
public interface IHeartbeat : IDisposable
{
    event EventHandler<HeartbeatLostEventArgs>? HeartbeatLost;        // 中断超阈值 → 安全状态
}

/// <summary>任务结束通知（INotifyTaskEnd，SAMPLE_POSITIONED 等事件）。</summary>
public interface INotifyTaskEnd
{
    event EventHandler<TaskEndEventArgs>? TaskEnded;
}
```

DI 注册（App.xaml.cs，实现类后续落地）：`services.AddSingleton<ISendTask, ModbusSendTask>()` 等——替换 OPC UA 时只换实现注册，Workflow 零改动。

## 9. 命令握手时序（任务清单 26–27 行）

```
Workflow                DeviceInterface(Modbus)              PLC
   | SendCycleStartAsync ──→ 写命令寄存器 ──────────────────→ |
   |                        ←─ 读应答寄存器（接受/拒绝）───── |
   |   （拒绝 → 错误码上报，不重试同参数）                     |
   | GetTaskStatusAsync    ──→ 轮询状态寄存器 ─────────────→ |
   |                        ←─ 状态变化检测（步骤推进）────── |
   | INotifyTaskEnd        ←─ SAMPLE_POSITIONED / 完成 ───── |
```

超时、重试次数、轮询周期全部来自 `WorkflowOptions`。

## 10. 错误处理与审计

- 全部异常走 `IErrorCollector.Collect`，携带 TaskId/SampleId/步骤上下文；错误码引用既有表：1001 抓取失败、1002 样品未检测到、2001 下压异常、3001 安全门、4001 PLC 心跳超时、5003/5004 入库写库、9001 急停——**禁止 magic number，新增先登记开发规范 11.1**；
- 每步起止时间、SCT 触发原因、超差详情、系数变更前后、人工干预写轨迹日志（任务清单第 12 节追溯要求）；
- 状态转移守卫拒绝非法转移时同样记录（暴露编排缺陷）。

## 11. 对外接口（草案）

```csharp
/// <summary>流程编排器（状态机驱动 + 设备编排；设备访问经 DeviceInterface 抽象）。</summary>
public interface IWorkflowOrchestrator : IAsyncDisposable
{
    /// <summary>当前任务状态（供 Dashboard 任务队列/当前任务卡绑定）。</summary>
    WorkflowSnapshot Snapshot { get; }

    Task StartAsync(CancellationToken ct);      // 启动主循环（Auto 模式）
    Task StopAsync(CancellationToken ct);       // 优雅停止（完成当前步后暂停）
    Task EmergencyStopAsync(string reason);     // 急停（§7.4）
    event EventHandler<WorkflowStateChangedEventArgs>? StateChanged;   // 状态变化上抛 UI
}
```

`WorkflowSnapshot`（TaskId、状态机当前态、SCT 子状态、当前样品、各锁占用）即主控台"当前任务卡 + 任务队列卡"的数据源（Dashboard 绑定另行落地）。

## 12. 测试要点（开发规范第 9 节：Workflow 状态机转移必测）

- 状态机转移穷举：合法链全通 + 所有非法跳跃被拒；
- 生产九步：每步失败分支（预检失败/设备超时/重试耗尽→报警）；
- SCT 编排：三类任务 ID_CYCLE 映射、测高特殊流（不激发）、CompleteTask 与 Notify 事件回喂断言；
- 锁：互斥（并发获取阻塞）、CoefficientLock 读写并行、锁超时；
- 急停：Emergency → 队列清空 + Running 终止 → 复位恢复路径；
- 设备抽象：`FakeDeviceGateway`（tests/ 新增，可编排应答序列/模拟超时），复用既有 Fake 仓储与 `FakeSctPolicyStore`。

## 13. 不在本期范围（另行设计）

- Modbus 寄存器映射表、报文与连接管理具体实现（DeviceInterface 的 ModbusTcp 实现）；
- GNR 协议号 100–140 报文解析；
- Vision 视觉选点算法（Workflow 只留接口挂点）；
- Dashboard"当前任务/任务队列"卡片绑定 `WorkflowSnapshot`（Workflow 实现后另行落地）；
- SctStrategyEngine 本体实现（见引擎设计文档）。

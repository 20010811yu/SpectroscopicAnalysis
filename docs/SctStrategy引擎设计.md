# SctStrategyEngine 策略引擎设计（需求）

> 依据：[上位机任务清单.md](上位机任务清单.md) 第五节（SCT 策略）、第七节（状态机枚举）、[开发规范.md](开发规范.md)、[SCT管理页面设计.md](SCT管理页面设计.md)。
> 范围：**触发判断与样品选择、优先级调度队列、超差闭环与测高联动**。设备通信（Modbus 寄存器映射、GNR 协议）不在本文范围，仅预留挂点接口。

## 1. 背景与定位

SCT 管理页已落地策略配置（`SctPolicyOptions` → `config/sct_policy.json`）、标样维护与执行记录展示，但自动触发/调度/闭环逻辑没有执行者。`SctStrategyEngine` 是 SCT 自动化逻辑的"大脑"，位于 `SctStrategy/` 模块，在分层中处于：

```
UI → Workflow → SctStrategyEngine（本文） → DeviceInterface / 数据层（仓储接口）
```

- 策略引擎**只做决策**（何时触发、选哪个样品、任务怎么排队），**不做执行**（驱动 PLC/光谱仪由 Workflow 与 DeviceInterface 负责）；
- 依赖方向：引擎依赖抽象接口（仓储、策略配置、时间源），禁止反向依赖 UI/Workflow 具体实现；
- 纯逻辑可测：不引用 WPF 类型，所有外部依赖走接口，测试用 Fake 注入（复用 `tests/` 既有 Fake 模式）。

## 2. 职责边界

| 职责 | 说明 |
|---|---|
| 触发判断 | 按策略配置判断何时插入标准化/控制检查/类型标准化任务 |
| 样品选择 | 从标样库选出本次任务的目标样品（设置样/控制样/类型标准化样） |
| 优先级调度 | 维护任务队列，按 P29 固定优先级次序出队 |
| 超差闭环 | 控制样超差 → 标准化 → 重测验证 → 恢复/报警暂停 的状态推进 |
| 测高联动 | 使用次数达阈值 → NeedMeasure → 插入测高任务 → 厚度流转 |
| 审计留痕 | 每次触发/选样/闭环判定写 `SCT_OperationLog`（经 `ISctOperationLogRepository`） |

**不做**：机器人/激发控制、系数计算本身（校正模块负责）、寄存器读写、UI 更新（经事件/回调上抛）。

## 3. 输入与依赖

| 依赖 | 来源 | 用途 |
|---|---|---|
| `SctPolicyOptions` | `ISctPolicyStore`（已有） | 全部策略参数 |
| `ISctSampleRepository` | 已有 | 标样档案（厚度/次数/状态/合金/架位） |
| `ISctOperationLogRepository` | 已有 | 审计写入 + 历史查询（轮换选样依据） |
| `IErrorCollector` + `ErrorCodes` | LoggingAlarm（已有） | 统一错误链；新增错误码先登记开发规范 11.1 |
| `TimeProvider` | .NET 8 抽象 | 定时触发判定可测（测试注入假时钟） |

## 4. 核心模型：SctTask

```csharp
/// <summary>SCT 任务类型（与 CYCLE_START_REQUEST 的 ID_CYCLE 对齐）。</summary>
public static class SctTaskType
{
    public const string Standardize     = "Standardize";     // 设置样标准化（ID_CYCLE=2）
    public const string ControlCheck    = "ControlCheck";    // 控制样检查（ID_CYCLE=3）
    public const string TypeStandardize = "TypeStandardize"; // 类型标准化
    public const string HeightMeasure   = "HeightMeasure";   // 测高
    public const string Production      = "Production";      // 生产样品分析（ID_CYCLE=1）
    public const string Archive         = "Archive";         // 归档
}

/// <summary>任务状态（扩展任务清单第 7 节任务状态，SCT 任务生命周期）。</summary>
public static class SctTaskStatus
{
    public const string Queued    = "Queued";    // 已入队
    public const string Running   = "Running";   // 已出队下发执行
    public const string Completed = "Completed";
    public const string Failed    = "Failed";
    public const string Cancelled = "Cancelled"; // 被更高优先级/急停取消
}
```

任务携带：`TaskId`、`TaskType`、`SampleId`（可空，生产任务为样品编号）、`Reason`（触发原因，写入执行记录）、`EnqueueTime`、`Priority`（由类型映射，见 §6）、`Status`。

**命名约束**：状态与类型枚举命名必须与任务清单第 7 节一致，不得自创（AGENTS 规则 4）。

## 5. 触发判断（消费 SctPolicyOptions）

引擎对外暴露事件入口，各入口按配置判断是否产生任务：

| 入口事件 | 判断规则 | 配置项 | 产物 |
|---|---|---|---|
| `NotifyAnalysisCompleted` | 每完成 N 个生产样插入控制样 | `ControlInsertMode == ByCount`、`ControlInsertCount` | ControlCheck 任务 |
| 时钟节拍（每分钟） | 距上次控制检查超时 | `ControlInsertMode == ByTime`、`ControlInsertMinutes` | ControlCheck 任务 |
| 时钟节拍（每小时） | 距上次标准化超时 | `PeriodicStandardizeEnabled`、`PeriodicStandardizeHours` | Standardize 任务 |
| `NotifyModeChanged(auto)` | 切入自动模式 | `StandardizeOnAutoMode` | Standardize 任务 |
| `NotifyControlResult` | 控制样超差（见 §7） | `LimitType`、`LimitValue`、`AutoStandardizeOnControlFail` | Standardize 任务 |
| `NotifyAlloySwitched` | 合金类型切换 | `TypeStandardizeOnAlloySwitch` | TypeStandardize 任务 |
| 时钟节拍（每日） | 距上次类型标准化超期 | `TypeStandardizePeriodEnabled`、`TypeStandardizePeriodDays` | TypeStandardize 任务 |
| `NotifyAnalysisCompleted` | 标样使用次数达阈值（见 §8） | 样品档案 `MaxUsageCount` | HeightMeasure 任务 |
| `SubmitTask`（手动） | SCT 页手动触发按钮（挂点已留） | — | 对应任务，理由"人工触发" |

判定实现为纯函数（输入：配置 + 状态快照 + 事件参数；输出：任务或 null），便于单元测试穷举配置组合。

## 6. 优先级调度队列

### 6.1 优先级（P29 固定次序，不可配置）

| 优先级 | 任务 | 说明 |
|---|---|---|
| 0（最高） | 急停/故障恢复 | 由 Workflow 直接处理，不经本队列，但入队任务全部置 Cancelled |
| 1 | 超差触发的 Standardize | `Reason = 控制样超差` |
| 2 | ControlCheck | |
| 3 | TypeStandardize | |
| 4 | HeightMeasure | 测高是标样可用的前置条件，优先于生产 |
| 5 | Production | 生产样品分析 |
| 6（最低） | Archive | 归档 |

> 注意：手动触发的标准化按其 `Reason` 归入对应档位（人工请求视同超差档 1，保证"优先级高于生产分析"——任务清单第 5 节⑥）。

### 6.2 调度规则

- **同优先级 FIFO**；`SubmitTask` 返回 `TaskId`，重复目标样品的同类型未完成任务**去重合并**（避免重复入队）；
- **互斥**：同一时刻仅一个任务 `Running`（RobotLock/SparkStandLock 由 Workflow 持有，引擎保证串行出队）；
- **暂停规则**：SCT 任务（1–4 档）运行期间暂停生产样品分析（P19），生产任务保持在队列中；
- **失败处理**：任务 `Failed` 不自动重试，经 `IErrorCollector` 上报（带 TaskId/SampleId/步骤上下文），由人工或闭环逻辑决定后续；
- 出队接口：`bool TryDequeue(out SctTask task)` 由 Workflow 轮询/事件驱动调用，引擎不做 I/O。

## 7. 超差闭环（P17）

```
控制样分析结果 → 判定 |result - certified| 是否超差
  ├─ 未超差 → 记录 ControlCheck 日志（IsPass=true），生产继续
  └─ 超差（按 LimitType/LimitValue 判定）
       ├─ AutoStandardizeOnControlFail = false → 报警，暂停自动模式
       └─ true → 入队 Standardize（优先级 1，Reason=控制样超差）
             ↓ 标准化完成（系数版本化 +1，审计留痕）
             → 重测同一控制样（入队 ControlCheck，Reason=超差验证重测，标记 recheck）
                  ├─ 通过 → 记录日志，闭环结束，恢复生产
                  └─ 仍超差 → recheck 次数 < ControlRecheckMaxCount？
                        ├─ 是 → 再次 Standardize → 重测（循环）
                        └─ 否 → 报警 + 暂停自动模式（需人工介入）
```

- 闭环状态由引擎持有（当前超差的控制样编号、已重测次数），标准化完成事件 `NotifyStandardizeCompleted` 推进状态；
- **系数应用原则**（P96）：重测判定始终用最新 `IsCurrent` 系数版本，原始值与校正值双存；
- 每次"再标准化"前写审计日志（前后系数、原因=超差重测第 N 次）。

## 8. 测高联动（P13–P14）

```
标样被调度执行（任何分析类任务选中某标样）
  → 使用次数 UsageCount + 1（写库，事务）
  → UsageCount ≥ MaxUsageCount？
       ├─ 否 → 正常执行
       └─ 是 → 置 Status = NeedMeasure（写库 + 审计），若该标样是当前任务目标：
             1) 生产样不受影响（仅标样自身）
             2) 该标样的分析任务取消，改插 HeightMeasure 任务（优先级 4）
             3) 测高完成（手动登记或自动测高回填）→ 厚度流转（SctViewModel 同规则）：
                  厚度 < MinThickness → Disabled + 报警更换
                  厚度 ≥ MinThickness → Normal，并发出"重算抓取高度"挂点事件（Workflow → PLC）
```

- 选样阶段过滤：**NeedMeasure/Disabled 的标样不参与选样**（见 §9 选样规则前置过滤）；
- 引擎不执行测高动作，只负责"插入测高任务 + 消费测高结果事件 `NotifyHeightMeasured(sampleId, thickness)`"。

## 9. 样品选择规则

选样在任务出队、下发生成时执行，规则来源任务清单第 5 节②③⑤：

| 任务类型 | 候选过滤（前置） | 选择规则 | 无可用时 |
|---|---|---|---|
| Standardize | `SampleType=SettingUp`、`Status=Normal`、`IsActive`、匹配当前分析方法 | `SettingSampleSelectionMode`：`LeastUsed`（UsageCount 最少）/ `RoundRobin`（按上次使用时间最早轮询） | 报警（登记错误码）+ 暂停分析 |
| ControlCheck | `SampleType=Control`、`Status=Normal`、`IsActive` | `ControlMatchByAlloy` → 按 AlloyType 匹配生产样；`ControlRotationEnabled` → 多块轮换（选最久未用） | 同上 |
| TypeStandardize | `SampleType=TypeStd`、`Status=Normal`、`IsActive`、AlloyType **精确匹配**目标合金 | 同设置样规则 | 同上 |
| HeightMeasure | 目标样品即任务携带的 SampleId | 不选择 | — |

## 10. 对外接口（草案）

```csharp
/// <summary>SCT 策略引擎（纯决策，不做设备 I/O；实现须可单测）。</summary>
public interface ISctStrategyEngine
{
    /// <summary>提交任务（手动触发挂点 / 外部请求）；重复目标去重合并，返回 TaskId。</summary>
    int SubmitTask(string taskType, string? sampleId, string reason);

    /// <summary>尝试出队下一个可执行任务（优先级序）。</summary>
    bool TryDequeue(out SctTask task);

    /// <summary>任务完成/失败回执（Workflow 执行后回调）。</summary>
    void CompleteTask(int taskId, bool success, string? error);

    // ---- 事件入口（§5 触发判断） ----
    void NotifyAnalysisCompleted(string sampleId);          // 生产样完成（计数/控制样插入/次数联动）
    void NotifyControlResult(string sampleId, bool passed); // 控制样判定结果（§7 闭环）
    void NotifyStandardizeCompleted(string sampleId);       // 标准化完成（闭环推进）
    void NotifyHeightMeasured(string sampleId, double thickness); // 测高结果（§8 流转）
    void NotifyModeChanged(bool autoMode);                  // 系统模式切换
    void NotifyAlloySwitched(string alloyType);             // 合金类型切换
    void OnTick();                                          // 时钟节拍（定时/按时间触发）

    /// <summary>急停/故障：清空队列（全部 Cancelled），冻结闭环状态。</summary>
    void EmergencyReset();
}
```

## 11. 错误处理与审计

- 全部异常走统一错误链（`IErrorCollector.Collect`），携带 TaskId/SampleId/步骤上下文；禁止空 catch；
- 新错误码（建议，使用前先登记开发规范 11.1）：
  - `5101` 设置样选择无可用样品（报警暂停分析）
  - `5102` 控制样超差闭环重测耗尽（报警暂停自动模式）
  - `5103` 类型标准化样无匹配（合金无对应标样）
  - `5104` 测高后厚度低于最小厚度（报废更换报警）
- 触发原因、选样结果、闭环每一步均写 `SCT_OperationLog`（TriggerReason/ResultStatus/系数前后值），满足任务清单第 12 节追溯要求。

## 12. 测试要点

- 触发判断纯函数穷举：`SctPolicyOptions` 各字段组合（模式 × 开关 × 阈值边界值 ±1）；
- 选样：LeastUsed/RoundRobin、合金匹配、NeedMeasure/Disabled 过滤、无可用报警路径；
- 队列：优先级次序、同优先级 FIFO、去重合并、急停清队；
- 闭环：超差 → 重测 N 次 → 恢复 / 耗尽报警（`ControlRecheckMaxCount` 边界）；
- 测高联动：达阈值置 NeedMeasure → 测高结果两种流转；
- 复用 `tests/` 既有 Fake（`FakeSctPolicyStore`、`FakeSampleRepository`、`FakeOperationLogRepository`、`FakeUiDispatcher`）+ 假 `TimeProvider`。

## 13. 不在本期范围（另行设计）

- Modbus 寄存器映射、模式寄存器轮询与命令握手（DeviceInterface 模块）；
- GNR 光谱仪 TCP 协议对接；
- 抓取高度重算的下发执行（引擎只发挂点事件）；
- 手动触发按钮启用（引擎实现并 DI 注册后，解除 `SctViewModel` 三个挂点命令的 `NeverExecute`）。

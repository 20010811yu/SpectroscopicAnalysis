# SCT 管理页面设计

依据：《自动直读光谱系统工艺流程和系统设计要求》第四～六节（SCT 样品库控制与选择逻辑）、`docs/上位机任务清单.md` 第 5 节（SCT 策略引擎）、`docs/界面设计规范.md`。

## 1. 页面定位与分工

| 页面 | 职责 |
|---|---|
| 样品档案（SamplesPage） | 标样/生产样**档案管理**：录入、删除、标准值/控制限等 7 张表的全量浏览 |
| **SCT 管理（本页）** | **运行视角**：状态监控概览、标样运行维护（测高登记/阈值调整/启用禁用）、执行记录查看、策略参数配置 |

## 2. 功能清单（四大功能块）

### 2.1 SCT 状态监控概览
- 统计卡片 ×4：设置样数量 / 控制样数量 / 类型标准化样数量 / 待维护数（NeedMeasure + Disabled）。
- 最近一次标准化、最近一次控制检查的结果摘要（样品、时间、结果状态），取自 SCT_OperationLog 最新记录。

### 2.2 SCT 标样库管理（运行维护）
- 列表：编号、名称、类型（徽章）、合金、架位、当前/初始厚度、使用次数、最小厚度、状态（徽章）、上次测高、上次使用、启用。
- 类型筛选（全部/设置样/控制样/类型标准化样）+ 关键词搜索 + 分页（每页 20 条，复用样品档案分页交互）。
- **测高登记**：选中行录入实测厚度，按状态机（PDF 3.2）流转：
  - 厚度 < MinThickness → Status = Disabled（报废阈值）；
  - 否则 → Status = Normal（清 NeedMeasure），并更新 LastHeightCheckTime；
  - 写 HeightMeasure 操作日志（ThicknessBefore/After，审计留痕）。
- **阈值调整**：修改最小厚度 / 最大使用次数（二次确认；写库 Update）。
- **启用/禁用**：IsActive 切换（危险操作二次确认）。
- 入库初始化与删除仍在样品档案页；本页不重复。

### 2.3 SCT 执行记录
- 只读列表（按操作时间倒序 + 分页）：时间、样品、操作类型（标准化/控制检查/测高）、触发原因、结果状态、厚度前后、系数前后、合格判定。

### 2.4 手动触发与策略配置
- **手动触发**（挂点）：标准化 / 控制检查 / 测高按钮 UI 就绪，`CanExecute = false`，Tooltip「待 SctStrategyEngine 接入后启用」；接入时绑定策略引擎的 SubmitTask 入口（优先级见任务清单第 5 节⑥）。
- **策略配置**（持久化 `config/sct_policy.json`，经 `ISctPolicyStore`；对应任务清单第 5 节）：
  - 设置样选择规则：使用次数最少（LeastUsed）/ 轮询（RoundRobin）（规则②）；
  - 控制样插入模式：按数量（每 N 个生产样）/ 按时间间隔（分钟）（规则③）；
  - 控制样轮换：多块轮换开关、按合金类型匹配开关（规则③）；
  - 控制样判定：限类型（Absolute / Percent / Sigma）+ 限值（规则④）；
  - 超差闭环：标准化后验证重测次数上限（1–5，仍失败 → 报警并暂停自动模式）（规则④）；
  - 标准化触发项：控制样超差自动标准化、切换自动模式标准化、定时标准化（小时）开关（规则①）；
  - 类型标准化触发：合金类型切换时触发、期限届满触发（天）开关（规则⑤）。

## 3. 布局结构（界面设计规范）

```
Grid(4 行: Auto/Auto/*/Auto, Margin 16)
├─ 行0  页标题「SCT 管理」
├─ 行1  概览行：UniformGrid 4 列 Star 统计卡（Card 样式）+ 2 张摘要卡
├─ 行2  标样库 Card（Star 占满）
│   ├─ 工具栏：类型筛选 ModeBtn 组 + 搜索框 + 查询按钮 + 测高/阈值/启用禁用按钮（选中行可用）
│   ├─ 维护表单卡（测高/阈值，按开关显隐，同样品档案录入卡模式）
│   ├─ DataGrid（GridRow/GridCell/GridColumnHeader，只读，状态徽章 DataTrigger）
│   └─ 分页栏（首页/上一页/页码/下一页/共 N 条）
└─ 行3  执行记录 Card + 策略配置 Card（左右各半 Star 列）
```

状态徽章颜色语义（Brush.Status.*，不得挪用）：Normal→Idle、NeedMeasure/Warning→Running（警示色）、Disabled→Failed。

## 4. 数据来源与接口映射

| 功能 | 接口 | 说明 |
|---|---|---|
| 标样库列表/筛选 | `ISctSampleRepository.GetAll()` | VM 内存筛选（类型+关键词）+ 分页 |
| 测高登记 | `ISctSampleRepository.Update()` + `ISctOperationLogRepository.Insert()` | 状态流转 + 审计日志（事务性：先 Update 后 Insert，异常走 IErrorCollector） |
| 阈值调整/启用禁用 | `ISctSampleRepository.Update()` | 二次确认（IDialogService） |
| 执行记录 | `ISctOperationLogRepository.GetAll()` | 倒序 + 分页 |
| 概览统计 | 上两者内存聚合 | |
| 策略配置 | `SctStrategy.ISctPolicyStore`（JsonSctPolicyStore） | `config/sct_policy.json`，加载失败回退默认值 |

## 5. 与 SctStrategyEngine 的接入挂点（后续迭代）

1. 手动触发三按钮 → `ISctStrategyEngine.SubmitTask(TaskType.*)`（优先级队列）。
2. 策略配置对象 `SctPolicyOptions` → 策略引擎构造注入，作为触发判断依据。
3. SCT 子状态进度条（Selecting→…→Returning）接入 WorkflowOrchestrator 状态机后在概览卡下方追加。
4. 执行记录新增来源：引擎执行 Standardization/ControlCheck/HeightMeasure 后写日志，本页自动呈现。

## 6. 约束

- 枚举命名沿用 `SampleStatus`/`SampleType` 常量（任务清单第 7 节），不自创状态名。
- 新错误码先登记 `LoggingAlarm/ErrorCodes` 再使用；不含任何身份内容（规则 11）。
- VM 不引用 WPF 类型；I/O 走 Task.Run + IUiDispatcher；危险操作二次确认。

# SCT 任务机制总结

> 生成时间：2026-11-27 · 基于当前代码实现梳理（非规范文档，规范以 `docs/上位机任务清单.md`、`docs/开发规范.md` 为准）

## 一、样品注册（`src/SpectroscopicAnalysis.Host/SampleManager/SampleRegistrationService.cs`）

- **初始厚度非必填**：填了须 >0 且 > 最小厚度；留空合法。SCT 样必填架位、最小厚度、最大使用次数。
- **已填初始厚度** → `CurrentThickness = InitialThickness`，状态 `Normal`、激活。
- **未填初始厚度** → 存 **0 哨兵**（展示显示 "—"），状态置 `NeedMeasure`、不激活，并**自动入队 HeightMeasure 测高任务**（reason 用专用前缀 `ReasonRegistrationMeasure`，定义于 `SctStrategy/SctTask.cs:28`）。
- **孪生样品**：检查样兼任类型标准化样时同批创建 `-T` 后缀孪生档案（常量 `TwinIdSuffix`），同架位、`PairSampleId` 互指，合金只落在孪生上（数据库触发器约束）；孪生复制主样状态（未填厚度同样 NeedMeasure）。

## 二、测高触发（`src/SpectroscopicAnalysis.Host/SctStrategy/SctStrategyEngine.cs`）

三条触发路径均置 NeedMeasure 并入队测高：

1. **注册未填初始厚度**（注册时）；
2. **阈值联动**：分析完成后（`CheckUsageThreshold`，:920-947）`Normal` 样 `UsageCount >= MaxUsageCount` → 置 NeedMeasure + 停用 + Warning 日志 + 自动入队测高（同类型同目标去重兜底）；
3. **孪生联动**：主样达阈值后孪生同步置 NeedMeasure，**各自独立入队**，各自经 `NotifyHeightMeasured` 闭环。

选样时 NeedMeasure 样仅作兜底候选（Normal 优先），执行前由编排器强制前置测高。

## 三、测高执行与结果处理（`src/SpectroscopicAnalysis.Host/Workflow/WorkflowOrchestrator.cs`）

- **执行**（:751-777）：机器人 `MeasureHeightAsync` → 回读厚度（未回读抛异常）→ `NotifyHeightMeasured` 回喂引擎。
- **首测回填**：`InitialThickness <= 0`（0 哨兵）时首测高度**同时作为初始厚度与当前厚度**（引擎 :557-561），状态恢复 `Normal`、激活、刷 LastHeightCheckTime。
- **过薄判定**：测得厚度 < `MinThickness` → `Disabled` + 停用 + 报警 `SctThicknessBelowMinimum`（"请更换"）；无自动恢复，仅测高合格路径可恢复 Normal。
- **守卫**：阈值重判仅对 Normal 样判定，NeedMeasure 样不会被误禁/误恢复。

## 四、编排兜底

- **内联前置测高**（:287-338）：Standardize/ControlCheck/TypeStandardize 执行前若样为 NeedMeasure，先测高；**已恢复 Normal → 跳过测高继续原任务**；不合格 → 任务内换样 `ReplaceTaskSample`，**换样上限 3 次**，仍不行则任务失败。
- **队列遗留跳过**（:343-353）：HeightMeasure 任务出队时若样品已在别处闭环（恢复 Normal 或已 Disabled）→ 直接成功完成，不重复测高。

## 五、任务队列与串联

- **固定优先级**（`SctStrategyEngine.cs:14-31`）：Standardize(1) > ControlCheck(2) > TypeStandardize(3) > **HeightMeasure(4)** > Production(5) > Archive(6)，同档 FIFO + 手动置顶（MoveToTop）；同时仅一个任务 Running，失败不自动重试。
- **测高→分析闭环**：注册测高合格后，编排器（WorkflowOrchestrator.cs:360-378）按样品类型补入后续分析任务：SettingUp→Standardize、Control→ControlCheck、TypeStd→TypeStandardize（reason "测高合格，注册分析任务自动入队"）。

## 六、状态枚举

| 类别 | 枚举值 | 位置 |
|---|---|---|
| 样品状态 | `Normal / NeedMeasure / Warning / Disabled` | `Data/Models/SctSample.cs:4-10` |
| 任务状态 | `Queued / Running / Completed / Failed / Cancelled` | `SctStrategy/SctTask.cs:32-48` |
| 任务类型 | `Standardize / ControlCheck / TypeStandardize / HeightMeasure / Production / Archive` | `SctStrategy/SctTask.cs:4-29` |
| SCT 子状态 | `Selecting→Retrieving→MeasuringHeight→Milling→Analyzing→UpdatingCoeff→Returning` | `Workflow/WorkflowStates.cs:97-123` |

MeasuringHeight 仅测高链经过，UpdatingCoeff 仅标准化链；主任务状态机 `Idle→Registered→Preparing→Milling→Placing→Analyzing→Archiving→Completed/Failed` 硬编码转移表（WorkflowStates.cs:8-95）。

## 七、货架（`src/SpectroscopicAnalysis.Host/SampleManager/RackDirectory.cs`）

- **SCT 样品货架：A01–E05**（5×5）；
- **生产样货架：P01–P25**（独立 5×5 编号空间），互不占用；
- 注册查重：生产样架位占用同时查 SCT 表与生产样表做防御，SCT 样仅查 SCT 表。

## 八、测试覆盖（`tests/SpectroscopicAnalysis.Tests/`）

- `SctStrategyEngineTests.cs`：NeedMeasure 兜底选样、阈值联动入队、首测回填初始厚度、恢复 Normal、过薄 Disabled、孪生联动。
- `WorkflowOrchestratorTests.cs`：测高回填、注册测高补分析任务、前置测高合格继续、不合格换样、无替代样失败。
- `SampleRegistrationServiceTests.cs`：NeedMeasure 注册+测高入队、孪生档案。
- 另有 `SctManualTriggerTests`、`SctPageTests`、`SctSampleRepositoryTests` 等。

## 总结

SCT 机制已完整落地：0 哨兵 + NeedMeasure 注册链路、阈值/孪生联动自动入队测高、内联前置测高 + 任务内换样兜底、首测回填初始厚度、过薄禁用状态机、注册测高→按类型补分析任务的编排闭环均有实现与单测覆盖，与任务清单 §三描述一致。

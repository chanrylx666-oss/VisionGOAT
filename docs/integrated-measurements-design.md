# 检测算子直接输出测量结果（0.6.0）

圆查找、直线查找、区域形状分析、直线卡尺、圆卡尺、模板匹配集成几何测量、数量统计、数量范围判定和图形标记，减少流程中的辅助节点。原有 `features` 端口、节点版本 1、旧配方和 C ABI 1 保持兼容。

每个检测算子增加五个输出：

| 端口 | 类型 | 含义 |
| --- | --- | --- |
| measurement | Measurement | 当前选择目标的完整测量记录及状态 |
| count | Int | 本次检测的目标数量 |
| found | Bool | 是否至少找到一个目标 |
| passed | Bool | 数量是否在模块设置的闭区间内 |
| overlay | Overlay | 模块生成的显示标记，可直接发布给 WPF |

`features` 始终保留所有目标。`resultIndex` 从 0 开始，仅选择测量记录，不删减检测列表。顺序沿用原算法的稳定排序。数量范围默认为 1–10000，可设置 0–0 检查目标不存在。`passed` 只是数量条件，不等同于精度、质量或整条配方通过；仅 inspection 配方显式发布为 decisionOutput 时用于整条检测判定。

测量记录固定包含 `featureKind, count, found, passed, selectedIndex, selectedValid, selected`。未找到目标或索引超出当前列表时 `selectedValid=false, selected=null`，禁止用全零坐标或上次测量代替。索引超出列表不改变数量判定。

圆输出原图坐标的圆心 x/y（同时提供 centerX/centerY）、radius、diameter、area、perimeter；直线输出端点、中心、length、angleDegrees；区域输出面积、周长、质心有效性、圆度、包围框；模板输出位置、中心、角度、尺度、评分、四个角点。卡尺另提供采样边缘点和拟合 RMS 误差。坐标、长度单位为像素，面积为像素平方，角度为度；图像坐标 Y 轴向下，正角顺时针。物理单位换算需要另行标定。

参数另包含 showOverlay、overlayColor、overlayLineWidth、labelResults。标记可显示测量文字，关闭标记不影响几何结果、计数、判定。所有参数可在编辑器中调整，保存到配方。

执行器按 node + port 储存并读取值，分支、公开输出、引用寿命必须遵守同一规则。所有执行过的检测节点的轻量测量记录存入 `measurements[nodeId]`，即使 Debug=false 也可从 SDK 按节点读取。失败、取消、超时清空公开输出及测量记录；不得通过成功节点的局部结果误判整条任务。保留旧 debugValues/debugOverlays 结构。

编辑器为多输出节点提供独立带名称端口，选中节点可直接查看数量和当前测量详情，结果标签页显示所有目标及中文字段。新增两节点圆查找示例，供 Qt 与 WPF 复用。SDK 增加类型化的公开/节点测量读取方法，并保留 GetCircles 等原方法。

验证：真实合成图的多圆测量、ROI/外部图像原点、端口分支类型与寿命、空图/索引越界、数量判定边界、配置标记、卡尺/直线/区域/模板、Debug=false、故障不残留测量、旧配方、Qt 可编辑参数与 WPF 相同配方读取。

用户追加：FindCircles 内集成 method=caliper 模式，复用 CircleCaliper 算法，参数包含 caliperCount、projectionWidth（宽度）、searchLength（高度）、removePointCount、maximumDeviation、minimumCoverage、caliperThreshold、sigma、polarity、edgeSelection、radius、startAngle/endAngle。默认 automatic 保持旧配方行为。剔除指定数量最大误差点，再迭代按最大偏差筛点重新拟合，最多 8 次；未收敛或剩余点不足不输出形状。新增 caliper 请求、采样、保留、实际剔除、缺失与剔除点坐标，成功形状的 EdgePoints 只包含最终保留点。标记包含保留点、剔除点叉形与可选测量文字。宽度须为奇数，剔除预算必须保留最低有效比例。

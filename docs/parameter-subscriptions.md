# 参数订阅、变量与计算函数

参数旁的 `↗` 可以把手动值切换为上游结果、公开输入、变量或计算表达式；`●` 表示已经订阅。可在同一窗口改回手动值。

## 圆查找带动圆环展开

1. 给圆查找和圆环展开连接图像输入。
2. 在圆环展开内画好圆环，设置环宽和角度。
3. 点击圆心 X 旁的订阅按钮，选择圆查找的 `measurement.selected.centerX`；圆心 Y 选择 `measurement.selected.centerY`。
4. 半径也可订阅 `measurement.selected.radius`，或用表达式计算内外半径。
5. 执行流程。调度器会先运行所有被引用的算子，无需增加可见的数据端口。

同一机制适用于矩形、旋转矩形、圆、多边形顶点和测量模块 A/B ROI 的数值字段，也适用于支持订阅的算法数值、整数和布尔参数。

已订阅的 ROI 禁止整块重画或拖动，避免手动操作覆盖绑定；可解除单字段订阅，或先解除此 ROI 的全部订阅再调整。未执行时显示保存的编辑预览值，执行后显示本次实际参数。预览值不会在执行时替代缺失结果。

## 全局变量、局部变量

打开 **编辑 → 变量与计算函数**。

- 全局变量在整个配方内可用，用于偏移量、比例系数、阈值和判定开关。
- 局部变量属于当前选中的算子，可使用手动值、上游字段、全局变量或表达式；不同算子可以使用相同局部变量名。
- 支持 `Double`、`Int`、`Bool`、`String`。名称使用英文字母、数字和下划线，不能以数字开头。
- 全局值为保存的初始值；WPF SDK 可以在每次运行时覆盖它们。覆盖不修改配方，也不会泄漏到下一帧或另一会话。
- 局部值每次执行重新计算，不是跨帧存储器；不能把未来结果写回变量形成循环。

## 表达式与纯函数

在订阅窗口选择表达式，给上游字段指定简单别名。例如将 `cx` 选择为圆查找圆心 X，再写：

```text
cx + global.OffsetX
```

引用局部变量使用 `local.Name`。管理窗口可定义函数，例如名称 `Shift`、形参 `x, dx`、函数体 `x + dx`，随后使用：

```text
Shift(cx, global.OffsetX)
r > 10 ? r + global.Margin : 20
```

支持 `+ - * / %`、比较、`&& || !` 和 `条件 ? 真值 : 假值`，以及 `abs/min/max/clamp/sqrt/pow/round/floor/ceil/sin/cos/tan/atan2/hypot`。`min/max` 各 2 个参数、`clamp` 3 个；角函数采用弧度，常数为 `pi`、`e`、`true`、`false`。条件必须是布尔值；不会把数字自动作为真假。整数参数要求结果为精确整数，可显式使用 `round/floor/ceil`。

函数可以调用其他函数，只访问形参和常数；不直接读取全局变量、局部变量或上游节点，需要把它们作为实参传入。函数是纯计算，不执行 C#/Python 脚本，也不能调用文件、网络或进程接口。字符串变量支持直接传值，不参与数值表达式。

## 坐标和缺失数据

检测结果的坐标属于其图像坐标平面。订阅 ROI 的 X/Y 时，系统在计算表达式后减去当前模块输入图的原点，因此裁剪后仍能使用原图圆心。半径、宽高不减原点。坐标表达式应输出目标的绝对位置，例如 `cx + offset`；若把 `x2 - x1` 这种相对差绑定到 X，就应先明确其预期位置含义。

原图和圆环展开图属于不同平面；两个独立展开分支也属于不同平面，不能直接交叉订阅几何坐标。可使用展开映射 API 显式转换后再设计流程。ROI 已采用位置修正参考坐标时，不叠加原图坐标订阅；应先切换为图像坐标基准，避免重复变换。

上游未选中目标或字段不可用时，实际读取它的下游跳过，检测判定为未知；不使用 0、上一帧值或编辑预览值。条件和逻辑运算惰性执行，未使用的表达式分支不会读取缺失结果。除零、越界、非法类型等错误会报告发生错误的接收算子。

## WPF SDK

```csharp
using var result = await session.RunAsync(frame, new RunRequest
{
    Variables = new Dictionary<string, object?>
    {
        ["OffsetX"] = 12.5,
        ["Margin"] = 8.0
    }
});
double offset = result.GetGlobalVariable<double>("OffsetX");
double center = result.GetLocalVariable<double>("unwrap", "CenterX");
JsonElement? actualParameters = result.GetResolvedParameters("unwrap");
```

变量必须先在配方中声明，传入未知变量或不兼容类型会被拒绝。局部值不可用时 `GetLocalVariable` 抛出异常，实际参数不可用时 `GetResolvedParameters` 返回 `null`。图像基准 ROI 返回输入局部坐标；显式使用教学参考坐标的 ROI 仍保留其声明的参考坐标。结果 JSON 的 `variables`、`nodes[id].localVariables`、`nodes[id].resolvedParameters` 可用于追溯本次值。

## 配方格式与兼容性

旧字段引用保持 `{ "node": "circle", "port": "result", "field": "measurement.selected.centerX" }`，公开输入保持 `{ "input": "Name" }`。

```json
{
  "variables": { "OffsetX": { "type": "Double", "value": 0.0 } },
  "functions": { "Shift": { "parameters": ["x", "dx"], "expression": "x + dx" } }
}
```

节点的 `variables` 使用相同类型和值结构。引用变量为 `{ "variable": "OffsetX", "scope": "global" }`，局部范围为 `local`。表达式为 `{ "expression": "Shift(cx, global.OffsetX)", "arguments": { "cx": { "node": "circle", "port": "result", "field": "measurement.selected.centerX" } } }`。

订阅对象可附加数字 `fallback` 用于编辑预览；它不参与运行时替代。变量、函数和绑定均参与内容哈希、保存、撤销与复制；复制节点会重映射内部引用。缺失来源、依赖循环、函数递归及不兼容类型会被拒绝。

配方全局变量最多 64 个、每节点局部变量最多 32 个、函数最多 64 个；每个函数最多 16 个形参、表达式最多 2048 字符、32 个别名。解析树深度、计算步数、函数调用深度和坐标来源数量均有限制。

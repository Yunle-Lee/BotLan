# Bot 动画模块

`BotAnimationEngine.Sample(t)` 根据时间生成动画帧，`BotView` 负责 WPF 绘制。

模块包含状态序列、形态、表情、视线、眼睛拟合、皮肤与装饰。帧采样逻辑不依赖 UI 时钟；`BotView` 隐藏和关闭时释放渲染回调。

```csharp
var frame = BotAnimationEngine.Sample(1, "idle", new BotOptions(Shape: "galet", Color: "bleu"));
```


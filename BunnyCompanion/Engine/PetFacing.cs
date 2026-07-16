namespace BunnyCompanion.Engine;

/// <summary>
/// 桌宠朝向：walk 等素材默认面朝画面<strong>左侧</strong>。
/// 向右移动时必须水平翻转（ScaleX=-1），否则会像倒着走。
/// </summary>
public static class PetFacing
{
    /// <summary>
    /// 按水平移动方向计算 ScaleX。
    /// moveDirection &gt; 0 向右，&lt; 0 向左。
    /// </summary>
    public static double ScaleXForMove(int moveDirection) =>
        moveDirection >= 0 ? -1.0 : 1.0;

    /// <summary>按拖拽水平位移计算朝向（拖向哪就面向哪）。</summary>
    public static double ScaleXForDragDelta(double deltaX) =>
        deltaX >= 0 ? -1.0 : 1.0;
}

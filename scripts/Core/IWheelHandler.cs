namespace TabletopSimulator.Core;

/// <summary>
/// 滚轮事件的优先拦截器。
///
/// 为什么需要它：<c>Alt+滚轮</c> 要用来无级旋转选中物件，而滚轮默认归相机缩放。
/// 如果让物件系统自己去抢 <c>_UnhandledInput</c>，输入处理就分散到两处，
/// 谁先拿到事件取决于节点顺序 —— 那种 bug 很难查。
/// 改成由输入路由统一分发：路由先问拦截器，拦截器说"我处理了"，相机就不动。
/// </summary>
public interface IWheelHandler
{
	/// <param name="steps">滚轮格数，正数向上。</param>
	/// <param name="screenPos">鼠标屏幕坐标。</param>
	/// <returns>是否已消费这次滚轮（true = 相机不要缩放）。</returns>
	bool HandleWheel(float steps, Godot.Vector2 screenPos);
}

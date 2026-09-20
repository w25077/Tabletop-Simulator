using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 「这个世界坐标下有什么东西」的查询接口。
/// 由输入路由调用，用来决定一次左键按下是"拖物件"还是"点空白"。
/// M1 没有物件，暂不设置；M2 的物件管理器会实现它。
/// </summary>
public interface IWorldPicker
{
	/// <summary>返回该世界点下最上层的可交互物件；没有则返回 <c>null</c>。</summary>
	Node2D? PickTopmost(Vector2 worldPos);
}

using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 左键拖拽的"被问"钩子（M5.5 P2：区域边框改大小）。
///
/// <b>为什么是钩子而不是让区域自己去监听输入：</b>
/// 这是本项目第一条设计约定（见 <c>docs/HANDOFF.md</c> 第 1 条）——
/// 输入只能有一个入口（<c>ViewportController</c>）。一旦区域系统、编辑器各自去
/// <c>_UnhandledInput</c> 里抢左键，谁先拿到事件就取决于节点顺序，
/// 那种 bug 极难查（M2 的 <c>IWheelHandler</c>、M3 的 <c>IZoneInteraction</c>
/// 都是为同一件事立起来的）。
///
/// 于是改成：<c>ViewportController</c> 在"左键按下"时<b>先问</b>这个钩子，
/// 它说"这一下归我"（返回 <c>true</c>）就不再起拖拽手势；
/// 之后的移动与松手由控制器继续转交给它，直到它说"我结束了"。
///
/// 这样"正在改大小"这个状态属于钩子的实现（<c>ZoneResizer</c>），
/// 而<b>事件的分发路径仍然只有一条</b>。
/// </summary>
public interface IPrimaryPressHook
{
	/// <summary>
	/// 左键按下。<b>返回 true = 这一次按下被接管</b>，控制器不再起普通拖拽手势
	/// （否则改大小的同时会把桌面上的牌拖走）。
	/// </summary>
	/// <param name="worldPos">按下点的世界坐标。</param>
	/// <param name="screenPos">按下点的视口坐标。</param>
	/// <returns>是否接管这次按下。</returns>
	bool TryBeginPrimaryDrag(Vector2 worldPos, Vector2 screenPos);

	/// <summary>左键按下后继续移动（只有接管成功的那次拖拽会走到这里）。</summary>
	/// <param name="worldPos">当前点的世界坐标。</param>
	void PrimaryDragTo(Vector2 worldPos);

	/// <summary>左键松开，结束这次拖拽。</summary>
	void EndPrimaryDrag();

	/// <summary>是否正处于"接管中"（控制器用它决定还要不要继续转发移动事件）。</summary>
	bool IsPrimaryDragActive { get; }
}

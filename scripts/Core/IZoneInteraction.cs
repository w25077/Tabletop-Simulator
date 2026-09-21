using System.Collections.Generic;
using Godot;
using TabletopSimulator.Core.Objects;

namespace TabletopSimulator.Core;
/// <summary>
/// 区域系统被物件系统"问"的接口。
///
/// 为什么是一个接口而不是让 <c>ZoneManager</c> 自己去抢输入：
/// 和 M2 的 <see cref="IWorldPicker"/> / <see cref="IWheelHandler"/> 完全同一个理由 ——
/// 输入只能有一个入口（<c>ViewportController</c>），区域要接管双击与右键，
/// 就更不能让两个节点各自监听 <c>_UnhandledInput</c>、靠节点顺序决定谁先拿到事件。
///
/// 于是改成：物件管理器在"自己没命中/没处理"时，按顺序问区域这几件事。
/// 返回 <c>true</c> = 区域已消费，物件系统就不再做默认处理。
/// 区域系统不存在（<c>null</c>）时这些功能整体关闭 —— 物件系统不需要知道区域存不存在。
/// </summary>
public interface IZoneInteraction
{
	/// <summary>
	/// 鼠标移动：让区域维护"当前悬停的区域"（`S` 洗牌 / `D` 抽牌的落点靠它）。
	/// 无返回值 —— 悬停只是状态更新，不涉及"谁消费这次事件"。
	/// </summary>
	void NotifyPointerMoved(Vector2 worldPos);

	/// <summary>右键：问区域要不要弹自己的菜单。</summary>
	/// <param name="worldPos">世界坐标（用来命中区域）。</param>
	/// <param name="screenPos">视口坐标（<c>PopupMenu.Position</c> 必须用它，见 M2 踩过的坑）。</param>
	/// <param name="hitObject">该点下命中的物件；没有则 <c>null</c>。
	/// 区域靠它判断"点到的是不是我自己那一摞里的牌"，从而决定要不要让位给物件菜单。</param>
	/// <returns>是否已接管（true = 不要再弹物件菜单或"全选"菜单）。</returns>
	bool TryHandleContextMenu(Vector2 worldPos, Vector2 screenPos, TabletopObject? hitObject);

	/// <summary>左键双击：问区域是不是"双击抽牌"的目标。</summary>
	/// <returns>是否已消费。</returns>
	bool TryHandleDoubleClick(Vector2 worldPos);

	/// <summary>
	/// 拖拽松手：问落点是不是落在某个区域里。
	/// 返回 <c>true</c> 表示<b>落点已由区域定夺</b>（接受则 <c>ZoneId</c> 与位置都已写好；
	/// 被拒则物件原地不动）。两种情况都不该再走自由堆逻辑 ——
	/// 否则会出现"既在区域里、又和桌上的牌粘成一堆"。
	/// </summary>
	/// <param name="dropped">本次松手拖拽的物件集合。</param>
	/// <param name="worldPos">落点的世界坐标。</param>
	/// <returns>落点是否位于某个区域内。</returns>
	bool TryHandleDrop(IReadOnlyList<TabletopObject> dropped, Vector2 worldPos);

	/// <summary>
	/// 键盘：问区域要不要处理这个键（`S` 洗牌 / `D` 抽牌），
	/// 落点按"悬停即为目标"解析 —— 鼠标指着哪个区域就作用到哪个。
	/// </summary>
	/// <returns>是否已消费（false = 交给物件系统继续处理）。</returns>
	bool TryHandleKey(InputEventKey key);

	/// <summary>
	/// 该物件所属的<b>叠放区域</b>（牌库 / 弃牌堆）；不属于、或该区域不是叠放排版时返回 <c>null</c>。
	///
	/// 用途是让物件系统把"一摞牌"当成一个整体来操作 ——
	/// 「悬停即为目标」在叠放语境下的含义是<b>指着这一摞</b>，
	/// 而不是"恰好被指到的那第 7 张牌"。整摞翻转与洗牌都靠它。
	/// </summary>
	Zone? StackZoneOf(TabletopObject obj);

	/// <summary>
	/// 拖拽<b>起点</b>：让区域成员先离开区域。
	///
	/// 必须在起点而不是落点做：落点可能不在任何区域里（拖到桌面），
	/// 那时若还留着 <c>ZoneId</c>，就会出现「它还在牌库里、位置却在桌面」的半截状态 ——
	/// 牌库张数会算错，下次重排还会把它吸回去。
	/// </summary>
	void BeginDrag(IReadOnlyList<TabletopObject> dragging);

	/// <summary>
	/// 物件即将被删除 / 清空：区域必须先松手。
	///
	/// 漏掉这一步的后果不是"数据不准"，而是<b>崩溃</b> ——
	/// <c>Zone.Members</c> 会留着已释放节点的引用，下一次排版就会碰到野指针。
	/// </summary>
	void ForgetObjects(IReadOnlyList<TabletopObject> objects);

	/// <summary>
	/// 收起区域自己的弹出菜单。
	/// 探针收尾必须调 —— <c>PopupMenu</c> 是 <c>Window</c>，弹出后会抢走后续的合成鼠标事件，
	/// 导致同一个二进制跑两次结果不同（M2 为此栽过一次）。
	/// </summary>
	void HideMenu();
}

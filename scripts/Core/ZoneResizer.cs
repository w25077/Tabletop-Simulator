using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 「拖动区域边框改大小」（M5.5 P2-4b）。
///
/// <b>它是 <see cref="IPrimaryPressHook"/>：不抢输入，而是被 <c>ViewportController</c> 问。</b>
/// 于是"正在改大小"这个状态活在这里，而事件的分发路径仍然只有一条
/// （见 <see cref="IPrimaryPressHook"/> 的说明与 <c>docs/HANDOFF.md</c> 第 1 条）。
///
/// 三件必须做对的事：
/// <list type="number">
/// <item><b>只改被拖的那条边。</b>拖左边 = 改原点 X、尺寸反着变；拖右下角 = 两条边一起。
///   这是 <see cref="Zone.ZoneHandle"/> 用位标志的原因 —— "动哪条边"可以直接与出来。</item>
/// <item><b>改矩形必须走 <c>Zone.ApplyDefinition</c></b>（经 <see cref="ZoneEditService.Mutate"/>）：
///   只改定义不重排成员的话，症状是"框变了、牌还在老地方"。</item>
/// <item><b>手抖不给过</b>：最小尺寸用 <see cref="ZoneEditService.MinDrawSize"/> ——
///   与"画区域"同一个下限，否则存在一种"拖出来的区域小到点不中"的状态。</item>
/// </list>
/// </summary>
public sealed class ZoneResizer : IPrimaryPressHook
{
	/// <summary>屏幕上大约多少像素之内算点在把手上（换算成世界单位时要除缩放）。</summary>
	private const float ScreenTolerancePx = 10f;

	private ZoneManager _zones = null!;
	private BoardCamera _camera = null!;

	private string _activeId = "";
	private Zone.ZoneHandle _handle = Zone.ZoneHandle.None;
	private Vector2 _startWorld;

	/// <summary>是否启用（编辑器开着、且停在「区域」页时才算）。</summary>
	public bool Enabled { get; private set; }

	/// <summary>是否正拖着某条边（自检读它；<c>ViewportController</c> 也用它决定要不要转发移动）。</summary>
	public bool IsPrimaryDragActive => _activeId.Length > 0;

	/// <summary>上一次改动是否因为"太小"被拒（自检用它验下限真的拦得住）。</summary>
	public bool LastResizeRejected { get; private set; }

	/// <summary>被拒的原因（空串 = 没被拒）。</summary>
	public string LastRejectReason => LastResizeRejected ? ZoneEditService.LastError : "";

	public void Bind(ZoneManager zones, BoardCamera camera)
	{
		_zones = zones;
		_camera = camera;
	}

	/// <summary>
	/// 开关把手与拖拽能力。由 <c>EditorPanel</c> 在开关面板 / 切页时调用 ——
	/// <b>把手是编辑期的东西</b>：平时桌上每块区域都挂着八个黄方块会很吵。
	/// </summary>
	public void SetEnabled(bool enabled)
	{
		if (Enabled == enabled)
			return;

		Enabled = enabled;
		SyncHandles();

		if (!enabled)
			Cancel();
	}

	/// <summary>
	/// 把 <see cref="Zone.HandlesVisible"/> 刷成当前该有的样子。
	///
	/// 分开成一个方法的原因：区域是<b>动态增删</b>的（画一块、删一块、读档换一桌），
	/// 所以"现在有没有把手"不能只在开关那一刻算一次 ——
	/// 开着编辑器新画一块区域，它的把手必须立刻出现。
	/// 由 <c>ZoneManager.ZonesChanged</c> 与开关两处共同驱动。
	/// </summary>
	public void SyncHandles()
	{
		foreach (Zone zone in _zones.AllZones)
		{
			bool wanted = Enabled;

			if (zone.HandlesVisible != wanted)
			{
				zone.HandlesVisible = wanted;
				zone.QueueRedraw();
			}

			if (!wanted && zone.HoveredHandle != Zone.ZoneHandle.None)
			{
				zone.HoveredHandle = Zone.ZoneHandle.None;
				zone.QueueRedraw();
			}
		}
	}

	/// <summary>中断当前拖拽（关面板 / 切页 / 收模式时调）。</summary>
	public void Cancel()
	{
		_activeId = "";
		_handle = Zone.ZoneHandle.None;
	}

	/// <summary>鼠标移动：维护"指着哪个把手"（只影响绘制，不改变任何状态）。</summary>
	public void NotifyPointerMoved(Vector2 world)
	{
		if (!Enabled)
			return;

		float tolerance = ScreenTolerancePx / Mathf.Max(_camera.ZoomLevel, 0.01f);

		foreach (Zone zone in _zones.AllZones)
		{
			Zone.ZoneHandle hit = zone.HitHandle(world, tolerance);
			Zone.ZoneHandle wanted = IsPrimaryDragActive && zone.Id == _activeId ? _handle : hit;

			if (zone.HoveredHandle != wanted)
			{
				zone.HoveredHandle = wanted;
				zone.QueueRedraw();
			}
		}
	}

	/// <inheritdoc/>
	public bool TryBeginPrimaryDrag(Vector2 worldPos, Vector2 screenPos)
	{
		LastResizeRejected = false;

		if (!Enabled)
			return false;

		float tolerance = ScreenTolerancePx / Mathf.Max(_camera.ZoomLevel, 0.01f);

		// 从<b>最后添加</b>的那块往回找：视觉上画在上面的那块应该先被点到，
		// 与 ZoneManager.ZoneAtWorld 的取向一致（重叠区域取最后添加的）。
		for (int i = _zones.AllZones.Count - 1; i >= 0; i--)
		{
			Zone zone = _zones.AllZones[i];
			Zone.ZoneHandle handle = zone.HitHandle(worldPos, tolerance);

			if (handle == Zone.ZoneHandle.None)
				continue;

			_activeId = zone.Id;
			_handle = handle;
			_startWorld = worldPos;
			return true;   // 接管这次按下：不再起普通拖拽手势
		}

		return false;
	}

	/// <inheritdoc/>
	public void PrimaryDragTo(Vector2 worldPos)
	{
		if (!IsPrimaryDragActive)
			return;

		if (_zones.FindById(_activeId) is not Zone zone)
		{
			Cancel();
			return;
		}

		Vector2 delta = worldPos - _startWorld;
		Rect2 rect = zone.Definition.Rect;

		float left = rect.Position.X;
		float top = rect.Position.Y;
		float right = rect.End.X;
		float bottom = rect.End.Y;

		if ((_handle & Zone.ZoneHandle.Left) != 0)
			left = Mathf.Min(rect.Position.X + delta.X, right - ZoneEditService.MinDrawSize);

		if ((_handle & Zone.ZoneHandle.Right) != 0)
			right = Mathf.Max(rect.End.X + delta.X, left + ZoneEditService.MinDrawSize);

		if ((_handle & Zone.ZoneHandle.Top) != 0)
			top = Mathf.Min(rect.Position.Y + delta.Y, bottom - ZoneEditService.MinDrawSize);

		if ((_handle & Zone.ZoneHandle.Bottom) != 0)
			bottom = Mathf.Max(rect.End.Y + delta.Y, top + ZoneEditService.MinDrawSize);

		var next = new Rect2(new Vector2(left, top), new Vector2(right - left, bottom - top));

		// 没有真的改变（例如已经顶到下限还被往小拖）就别走写回：
		// 那会白白重排一次成员、还会在撤销历史里留下一条"什么都没变"的记录。
		if (next.Position.IsEqualApprox(rect.Position) && next.Size.IsEqualApprox(rect.Size))
		{
			LastResizeRejected = next.Size.X <= ZoneEditService.MinDrawSize + 0.01f
				|| next.Size.Y <= ZoneEditService.MinDrawSize + 0.01f;
			return;
		}

		LastResizeRejected = !ZoneEditService.Mutate(zone, d => d.Rect = next);
	}

	/// <inheritdoc/>
	public void EndPrimaryDrag() => Cancel();

	/// <summary>自检入口：直接问"这个屏幕点会命中哪块区域的哪个把手"（不改任何状态）。</summary>
	internal (string ZoneId, Zone.ZoneHandle Handle) ProbeHandleAt(Vector2 screenPos)
	{
		Vector2 world = _camera.ScreenToWorld(screenPos);
		float tolerance = ScreenTolerancePx / Mathf.Max(_camera.ZoomLevel, 0.01f);

		for (int i = _zones.AllZones.Count - 1; i >= 0; i--)
		{
			Zone zone = _zones.AllZones[i];
			Zone.ZoneHandle handle = zone.HitHandle(world, tolerance);

			if (handle != Zone.ZoneHandle.None)
				return (zone.Id, handle);
		}

		return ("", Zone.ZoneHandle.None);
	}
}

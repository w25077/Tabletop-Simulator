using Godot;

namespace TabletopSimulator.Core;

/// <summary>
/// 桌面相机：只负责「看」这件事 —— 平移、以某个屏幕点为锚缩放、框取适配、复位。
///
/// 设计要点：<b>目标值与当前值分离</b>。
/// 所有数学（尤其是"保持鼠标下的世界点不动"）都作用在 <c>_target*</c> 上，
/// 每帧再用指数平滑把当前值逼近目标值。这样缩放平移的手感连续，
/// 又不会因为 Godot 的相机变换晚一帧生效而算错锚点。
/// </summary>
[GlobalClass]
public partial class BoardCamera : Camera2D
{
	[Export] public bool SmoothEnabled { get; set; } = true;

	/// <summary>平滑速度（越大越跟手）。0 附近≈瞬移，20 左右≈轻微惯性。</summary>
	[Export] public float PanSmoothSpeed { get; set; } = GameConfig.PanSmoothSpeed;

	[Export] public float ZoomSmoothSpeed { get; set; } = GameConfig.ZoomSmoothSpeed;

	[Export] public float MinZoom { get; set; } = GameConfig.MinZoom;
	[Export] public float MaxZoom { get; set; } = GameConfig.MaxZoom;

	/// <summary>缩放档位变化时发出（HUD 用它显示百分比）。</summary>
	[Signal] public delegate void ZoomChangedEventHandler(float zoom);

	private Vector2 _targetPosition;
	private Vector2 _targetZoom = Vector2.One;

	public Vector2 TargetZoom => _targetZoom;

	/// <summary>
	/// 目标位置。与 <see cref="Node2D.GlobalPosition"/> 的区别：后者是平滑逼近中的当前值，
	/// 会滞后；目标值在操作发生的瞬间就是最终值。测试与逻辑判断都应该读这个。
	/// </summary>
	public Vector2 TargetPosition => _targetPosition;

	/// <summary>目标缩放（线性倍率，1.0 = 100%）。</summary>
	public float ZoomLevel => _targetZoom.X;

	/// <summary>当前实际渲染的缩放（平滑过程中会落后于 <see cref="ZoomLevel"/>）。</summary>
	public float RenderedZoom => Zoom.X;

	public override void _Ready()
	{
		MakeCurrent();
		IgnoreRotation = true;
		AnchorMode = AnchorModeEnum.DragCenter;
		_targetPosition = GlobalPosition;
		_targetZoom = Vector2.One;
		Zoom = _targetZoom;
	}

	public override void _Process(double delta)
	{
		if (Zoom.IsEqualApprox(_targetZoom) && GlobalPosition.IsEqualApprox(_targetPosition))
			return;

		if (!SmoothEnabled)
		{
			SnapToTargets();
			return;
		}

		float f = (float)delta;
		float posT = 1f - Mathf.Exp(-PanSmoothSpeed * f);
		float zoomT = 1f - Mathf.Exp(-ZoomSmoothSpeed * f);

		GlobalPosition = GlobalPosition.Lerp(_targetPosition, posT);
		Zoom = Zoom.Lerp(_targetZoom, zoomT);

		// 收尾：避免指数逼近永远差一点点，导致每帧都在做无意义的重算。
		if (GlobalPosition.DistanceTo(_targetPosition) < 0.05f)
			GlobalPosition = _targetPosition;

		if (Mathf.Abs(Zoom.X - _targetZoom.X) < 0.0005f)
			Zoom = _targetZoom;
	}

	/// <summary>让当前值立刻等于目标值（开场定位、读档后定位用）。</summary>
	public void SnapToTargets()
	{
		GlobalPosition = _targetPosition;
		Zoom = _targetZoom;
	}

	// ---------------------------------------------------------------- 坐标换算

	private Vector2 ViewportCenter => GetViewportRect().Size * 0.5f;

	/// <summary>屏幕坐标 → 世界坐标（按目标值换算，不含平滑延迟）。</summary>
	public Vector2 ScreenToWorld(Vector2 screenPos) =>
		_targetPosition + (screenPos - ViewportCenter) / _targetZoom;

	/// <summary>世界坐标 → 屏幕坐标（按目标值换算）。</summary>
	public Vector2 WorldToScreen(Vector2 worldPos) =>
		(worldPos - _targetPosition) * _targetZoom + ViewportCenter;

	/// <summary>当前可见的世界矩形（用来做视口裁剪或"框选可见物件"）。</summary>
	public Rect2 VisibleWorldRect()
	{
		Vector2 half = ViewportCenter / _targetZoom;
		return new Rect2(_targetPosition - half, half * 2f);
	}

	// ---------------------------------------------------------------- 操作

	/// <summary>按屏幕像素位移平移画布（拖拽平移用）。</summary>
	public void PanByScreenDelta(Vector2 screenDelta)
	{
		if (screenDelta.IsZeroApprox())
			return;

		_targetPosition -= screenDelta / _targetZoom;

		if (!SmoothEnabled)
			SnapToTargets();
	}

	/// <summary>
	/// 以屏幕上某个点为锚点缩放：缩放前后，该屏幕点下方的世界坐标保持不变。
	/// 这是"滚轮缩放不跑偏"的关键。
	/// </summary>
	/// <param name="steps">滚轮格数，正数放大、负数缩小。</param>
	/// <param name="screenAnchor">锚点（一般就是鼠标位置）。</param>
	public void ZoomAtScreenPoint(float steps, Vector2 screenAnchor)
	{
		if (Mathf.IsZeroApprox(steps))
			return;

		float factor = Mathf.Pow(GameConfig.ZoomStep, steps);
		SetZoomInternal(_targetZoom.X * factor, screenAnchor);
	}

	/// <summary>设定绝对缩放倍率；<paramref name="screenAnchor"/> 为空则以屏幕中心为锚。</summary>
	public void SetZoomLevel(float zoom, Vector2? screenAnchor = null)
	{
		SetZoomInternal(zoom, screenAnchor ?? ViewportCenter);
	}

	private void SetZoomInternal(float requestedZoom, Vector2 screenAnchor)
	{
		float clamped = Mathf.Clamp(requestedZoom, MinZoom, MaxZoom);
		if (Mathf.IsEqualApprox(clamped, _targetZoom.X))
			return;

		Vector2 center = ViewportCenter;
		Vector2 worldBefore = _targetPosition + (screenAnchor - center) / _targetZoom;

		_targetZoom = new Vector2(clamped, clamped);

		Vector2 worldAfter = _targetPosition + (screenAnchor - center) / _targetZoom;
		_targetPosition += worldBefore - worldAfter;

		if (!SmoothEnabled)
			SnapToTargets();

		EmitSignal(SignalName.ZoomChanged, clamped);
	}

	/// <summary>
	/// 把一块世界矩形装进视野。
	/// </summary>
	/// <param name="worldRect">要适配的世界矩形。</param>
	/// <param name="padding">四周留白（世界单位，缩放前算）。</param>
	/// <param name="maxZoom">上限缩放，防止小桌面被放大到失真；0 表示用 <see cref="MaxZoom"/>。</param>
	public void FocusOnRect(Rect2 worldRect, float padding = 40f, float maxZoom = 0f)
	{
		if (padding != 0f)
			worldRect = worldRect.Grow(padding);

		Vector2 viewport = GetViewportRect().Size;
		if (worldRect.Size.X <= 0.01f || worldRect.Size.Y <= 0.01f ||
			viewport.X <= 0.01f || viewport.Y <= 0.01f)
			return;

		float fit = Mathf.Min(viewport.X / worldRect.Size.X, viewport.Y / worldRect.Size.Y);
		float ceiling = maxZoom > 0f ? Mathf.Min(maxZoom, MaxZoom) : MaxZoom;

		_targetZoom = new Vector2(Mathf.Clamp(fit, MinZoom, ceiling), Mathf.Clamp(fit, MinZoom, ceiling));
		_targetPosition = worldRect.GetCenter();

		if (!SmoothEnabled)
			SnapToTargets();

		EmitSignal(SignalName.ZoomChanged, _targetZoom.X);
	}

	/// <summary>把某个世界点居中（不改变缩放）。</summary>
	public void CenterOn(Vector2 worldPos)
	{
		_targetPosition = worldPos;
		if (!SmoothEnabled)
			SnapToTargets();
	}
}

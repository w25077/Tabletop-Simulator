using Godot;

namespace TabletopSimulator.Core.Objects;

/// <summary>
/// 框选时画的那个矩形。
///
/// 单独一个节点、<c>ZIndex</c> 抬高，这样它压在所有物件之上，
/// 又不参与物件集合的排序与拾取 —— 它本身永远不该被选中。
/// </summary>
public partial class SelectionBox : Node2D
{
	private static readonly Color FillColor = new(0.53f, 0.75f, 0.82f, 0.12f);
	private static readonly Color BorderColor = new(0.53f, 0.75f, 0.82f, 0.85f);

	private bool _active;
	private Vector2 _start;
	private Vector2 _end;

	public bool IsActive => _active;

	/// <summary>框选矩形（世界坐标，已归一化）。</summary>
	public Rect2 WorldRect
	{
		get
		{
			Vector2 min = _start.Min(_end);
			Vector2 max = _start.Max(_end);
			return new Rect2(min, max - min);
		}
	}

	public override void _Ready()
	{
		ZIndex = 500;
		Visible = false;
		SetProcess(false);
	}

	public void Begin(Vector2 worldPos)
	{
		_start = worldPos;
		_end = worldPos;
		_active = true;
		Visible = true;
		QueueRedraw();
	}

	public void Update(Vector2 worldPos)
	{
		if (!_active)
			return;

		_end = worldPos;
		QueueRedraw();
	}

	/// <summary>结束框选并返回最终矩形。</summary>
	public Rect2 Finish()
	{
		Rect2 rect = WorldRect;
		_active = false;
		Visible = false;
		QueueRedraw();
		return rect;
	}

	public void Cancel()
	{
		_active = false;
		Visible = false;
		QueueRedraw();
	}

	public override void _Draw()
	{
		if (!_active)
			return;

		Rect2 rect = WorldRect;
		DrawRect(rect, FillColor);
		DrawRect(rect, BorderColor, false, 2f);
	}
}

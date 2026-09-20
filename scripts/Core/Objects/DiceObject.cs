using System.Collections.Generic;
using Godot;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core.Objects;

/// <summary>
/// 骰子。<b>刻意不接物理引擎</b> —— 俯视 2D 桌面上，物理骰子的翻滚既看不清也难同步，
/// 而你需要的是「数字变化然后停下」这个结果。所以这里只做数字滚动。
///
/// 三段式动画：高速跳动 → 减速 → 定格脉冲。
/// 结果由 <see cref="RandomNumberGenerator"/> 产生并存下 seed，因此一次掷骰可复现
/// （调试时能重放，将来接联机也能同步）。
/// </summary>
public partial class DiceObject : TabletopObject
{
	private enum Phase
	{
		Idle,
		Rolling,
		Settling,
	}

	// ---- 动画时间线（秒）----
	private const double RollDuration = 1.10;
	private const double SettleDuration = 0.16;
	private const double FastInterval = 0.040;
	private const double SlowInterval = 0.280;

	private static readonly StyleBoxFlat Box = new();

	private readonly List<int> _final = new();
	private readonly List<int> _display = new();
	private readonly RandomNumberGenerator _rng = new();

	private Phase _phase = Phase.Idle;
	private double _elapsed;
	private double _nextTickAt;

	public override ObjectKind Kind => ObjectKind.Dice;

	/// <summary>面数（4 / 6 / 8 / 10 / 12 / 20 / 100，也接受任意 ≥2）。</summary>
	public int Sides { get; private set; } = 6;

	/// <summary>骰子个数（多骰，如 3d6）。</summary>
	public int Count { get; private set; } = 1;

	public bool IsRolling => _phase != Phase.Idle;

	/// <summary>是否至少完整掷出过一次结果。自检用它区分"还没掷"和"掷完停在某个值"。</summary>
	public bool SettledOnce { get; private set; }

	/// <summary>_Process 被调用的次数。自检诊断用 —— 能立刻区分"动画没跑完"和"压根没在跑"。</summary>
	public int ProcessTicks { get; private set; }

	/// <summary>滚动阶段已经过的时间（秒）。</summary>
	public double Elapsed => _elapsed;

	/// <summary>最终结果的点数之和。</summary>
	public int Total
	{
		get
		{
			int sum = 0;
			foreach (int v in _final)
				sum += v;
			return sum;
		}
	}

	/// <summary>最终结果（每个骰的点数）。</summary>
	public IReadOnlyList<int> Values => _final;

	/// <summary>实例级配置（面数 / 个数 / 随机种子）。</summary>
	public int Seed { get; private set; }

	/// <summary>当前外壳配色。自检用它做"像素颜色 = 期望颜色"的断言。</summary>
	public Color Tint => TintForSides(Sides);

	// ------------------------------------------------------------------ 配置

	public void Configure(int sides, int count, IReadOnlyList<int>? values = null, int seed = 0)
	{
		Sides = Mathf.Max(sides, 2);
		Count = Mathf.Clamp(count, 1, 10);

		if (seed != 0)
		{
			Seed = seed;
			_rng.Seed = (ulong)seed;
		}
		else
		{
			_rng.Randomize();
			Seed = unchecked((int)_rng.Seed);
		}

		_final.Clear();
		_display.Clear();

		for (int i = 0; i < Count; i++)
		{
			int v = values is not null && i < values.Count ? values[i] : _rng.RandiRange(1, Sides);
			_final.Add(Mathf.Clamp(v, 1, Sides));
			_display.Add(_final[i]);
		}

		_phase = Phase.Idle;
		SetProcess(false);
		UpdateSize();
		QueueRedraw();
	}

	/// <summary>因为多骰要横着排，宽度随个数增长。</summary>
	private void UpdateSize()
	{
		Vector2 baseSize = GameConfig.DefaultDiceSize;
		float width = Count <= 1
			? baseSize.X
			: baseSize.X * (1f + (0.62f * (Count - 1)));

		Size = new Vector2(width, baseSize.Y);
	}

	// ------------------------------------------------------------------ 掷骰

	/// <summary>开始掷。已在滚动中则忽略。</summary>
	public void Roll()
	{
		if (_phase != Phase.Idle)
			return;

		_rng.Randomize();
		Seed = unchecked((int)_rng.Seed);

		bool changed = false;
		for (int i = 0; i < Count; i++)
		{
			int v = _rng.RandiRange(1, Sides);
			if (_final[i] != v)
				changed = true;
			_final[i] = v;
			_display[i] = _rng.RandiRange(1, Sides);
		}

		_ = changed;
		_phase = Phase.Rolling;
		_elapsed = 0d;
		_nextTickAt = 0d;
		SetProcess(true);
		QueueRedraw();
	}

	/// <summary>跳过动画直接出结果（自检与调试用）。</summary>
	public void RollImmediate()
	{
		Roll();
		if (_phase == Phase.Rolling)
			Settle();
	}

	public override void _Process(double delta)
	{
		ProcessTicks++;

		if (_phase == Phase.Idle)
		{
			SetProcess(false);
			return;
		}

		_elapsed += delta;

		if (_phase == Phase.Rolling)
		{
			if (_elapsed >= RollDuration)
			{
				Settle();
				return;
			}

			if (_elapsed >= _nextTickAt)
			{
				// 间隔从 40ms 线性拉长到 280ms —— 这就是「逐渐停下来」的全部秘密
				double t = _elapsed / RollDuration;
				double interval = FastInterval + ((SlowInterval - FastInterval) * t);
				_nextTickAt = _elapsed + interval;

				for (int i = 0; i < Count; i++)
					_display[i] = _rng.RandiRange(1, Sides);

				QueueRedraw();
			}

			return;
		}

		// Settling：每帧重绘以驱动脉冲缩放
		if (_elapsed >= SettleDuration)
		{
			_phase = Phase.Idle;
			_elapsed = 0d;
			SetProcess(false);
		}

		QueueRedraw();
	}

	private void Settle()
	{
		_phase = Phase.Settling;
		_elapsed = 0d;
		SettledOnce = true;

		for (int i = 0; i < Count; i++)
			_display[i] = _final[i];

		QueueRedraw();
	}

	// ------------------------------------------------------------------ 绘制

	protected override void DrawContent()
	{
		Color textColor = ContrastTextFor(TintForSides(Sides));

		// 定格时做一个轻微放大的脉冲，强调"结果出来了"
		float pulse = 1f;
		if (_phase == Phase.Settling)
		{
			float t = (float)(_elapsed / SettleDuration);
			pulse = 1f + (0.12f * Mathf.Sin(t * Mathf.Pi));
		}

		Rect2 box = LocalRect.Grow(LocalRect.Size.X * (pulse - 1f) * 0.5f);

		DrawBody(box);

		if (Count <= 1)
		{
			DrawCenteredText(_display[0].ToString(), box.GetCenter(), GameConfig.DiceNumberFontSize, textColor);
			return;
		}

		// 多骰：上行列出各骰点数，下行显示合计
		var parts = new List<string>(Count);
		foreach (int v in _display)
			parts.Add(v.ToString());
		string sumLine = string.Join(" + ", parts);

		Vector2 center = box.GetCenter();
		float rowOffset = box.Size.Y * 0.19f;

		DrawCenteredText(sumLine, center - new Vector2(0f, rowOffset), GameConfig.DiceSumFontSize, textColor);
		DrawCenteredText($"= {Total}", center + new Vector2(0f, rowOffset), GameConfig.DiceTotalFontSize, textColor);
	}

	private void DrawCenteredText(string text, Vector2 center, int fontSize, Color color)
	{
		Font font = Fonts.Ui;
		Vector2 size = font.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize);
		float ascent = font.GetAscent(fontSize);
		float descent = font.GetDescent(fontSize);
		Vector2 pos = center + new Vector2(-size.X * 0.5f, (ascent - descent) * 0.5f);

		DrawString(font, pos, text, HorizontalAlignment.Left, -1f, fontSize, color);
	}

	/// <summary>画骰子本体。注意必须真的调用 DrawStyleBox —— 只配属性是不会画出东西的。</summary>
	private void DrawBody(Rect2 rect)
	{
		Box.BgColor = TintForSides(Sides);
		Box.BorderColor = GameConfig.DiceBorderColor;
		Box.DrawCenter = true;
		Box.ShadowSize = 0;
		Box.ShadowOffset = Vector2.Zero;
		Box.SetBorderWidthAll(Mathf.RoundToInt(GameConfig.DiceBorderWidth));
		Box.SetCornerRadiusAll(Mathf.RoundToInt(GameConfig.DiceCornerRadius));
		DrawStyleBox(Box, rect);
	}

	/// <summary>按面数上色 —— 桌上一眼就能认出哪个是 D20，不用放大看数字。</summary>
	private static Color TintForSides(int sides) => sides switch
	{
		4 => new Color("#a3be8c"),
		6 => new Color("#e5e9f0"),
		8 => new Color("#81a1c1"),
		10 => new Color("#b48ead"),
		12 => new Color("#d08770"),
		20 => new Color("#ebcb8b"),
		100 => new Color("#bf616a"),
		_ => new Color("#d8dee9"),
	};

	/// <summary>浅色底用深字，深色底用浅字。</summary>
	private static Color ContrastTextFor(Color background)
	{
		float luminance = (0.2126f * background.R) + (0.7152f * background.G) + (0.0722f * background.B);
		return luminance > 0.55f ? new Color("#2e3440") : new Color("#eceff4");
	}

	// ------------------------------------------------------------------ 状态

	protected override void CaptureExtra(ObjectState state)
	{
		state.DiceSides = Sides;
		state.DiceCount = Count;
		state.DiceValues = new List<int>(_final);
		state.DiceSeed = Seed;
	}

	protected override void ApplyExtra(ObjectState state)
	{
		Configure(state.DiceSides, state.DiceCount, state.DiceValues, state.DiceSeed);
	}
}

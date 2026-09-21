using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;
using TabletopSimulator.Core;
using TabletopSimulator.Data;

namespace TabletopSimulator.Dev;

/// <summary>
/// 存档 JSON 地基的自检（M4 第 1 步）。
///
/// <b>为什么先单独验这一层</b>：存档坏掉的表现极其难查 —— 读出来的牌"少了一个数值"、
/// 颜色"变成洋红"、区域"位置差了几像素"，都不会报错，只会在你验玩法时
/// 悄悄给出错误的结论。所以这一层要在往上盖东西<b>之前</b>先钉死。
///
/// 三条断言：
/// <list type="number">
/// <item><b>往返一致</b>：造一份"每个字段都设了非默认值"的定义 → 序列化 → 反序列化 →
/// 逐字段比对。这是主体。</item>
/// <item><b>可比性</b>：证明上一条第 2 步真的在比对 —— 把副本改一个字段，
/// 它必须报不一致。<b>没有这条，"往返一致"可能只是因为比对函数永远返回真。</b>
/// </item>
/// <item><b>新增字段覆盖</b>：用反射把定义类的公开属性与 <c>Clone()</c> 的结果对照。
/// 往 <c>CardDefinition</c> 加一个字段却忘了加进 <c>Clone()</c>，就会在这里变红 ——
/// 而那个字段十有八九也会被漏进 JSON。</item>
/// </list>
/// </summary>
internal static class DevJsonSim
{
	/// <summary>与其它自检节同样的写法：把"写键"和"读键"合成一次动作。</summary>
	private sealed class Checks
	{
		private readonly Godot.Collections.Dictionary _dict;
		private readonly List<string> _keys = new();

		internal Checks(Godot.Collections.Dictionary dict) => _dict = dict;

		internal void Put(string key, bool value)
		{
			_dict[key] = value;
			_keys.Add(key);
		}

		/// <summary>写不进聚合判定的数据（统计值、样本）。</summary>
		internal void Data(string key, Variant value) => _dict[key] = value;

		internal int Count => _keys.Count;

		internal int Passed()
		{
			int n = 0;
			foreach (string k in _keys)
			{
				if (_dict[k].AsBool())
					n++;
			}

			return n;
		}

		/// <summary>
		/// 全部断言都真才算过。<b>不写死条数</b> —— 早先这里写成 <c>Passed() == 16</c>，
		/// 于是以后每加一条断言都得回来改这个数字，忘了改就是"新断言全绿、聚合报红"，
		/// 正是"聚合键名漂移"那类错误的另一个化身。
		/// </summary>
		internal bool AllPass()
		{
			if (_keys.Count == 0)
				return false;

			foreach (string k in _keys)
			{
				if (!_dict[k].AsBool())
					return false;
			}

			return true;
		}
	}

	internal static Godot.Collections.Dictionary Probe()
	{
		var r = new Godot.Collections.Dictionary();
		var c = new Checks(r);

		CardDefinition card = SampleCard();
		TokenDefinition token = SampleToken();
		ZoneDefinition zone = SampleZone();
		BoardTheme theme = SampleTheme();

		// ---- 1. 往返一致 ----
		c.Put("card_roundtrip", RoundTrip(card, r));
		c.Put("token_roundtrip", RoundTrip(token, r));
		c.Put("zone_roundtrip", RoundTrip(zone, r));
		c.Put("theme_roundtrip", RoundTrip(theme, r));

		// 字段条数：**这几个数字是"给人看的提醒"，不是主要防线**。
		// 它们的作用是"加了字段之后自检会响一下"，逼你回来确认新字段：
		//   · 在 Clone() 里（由下面第 3 组断言钉住）
		//   · 往返一致（由第 1 组钉住）
		// 实测教训：这几个数字我第一次就写错了 —— ZoneDefinition 我数成 13，
		// 实际 14。不是靠眼睛重数改对的，而是把 json_field_names 打进报告、照着核对才数对。
		c.Put("card_field_count_ok", JsonProperties(typeof(CardDefinition)).Count == 9);
		c.Put("token_field_count_ok", JsonProperties(typeof(TokenDefinition)).Count == 12);
		c.Put("zone_field_count_ok", JsonProperties(typeof(ZoneDefinition)).Count == 14);
		c.Put("theme_field_count_ok", JsonProperties(typeof(BoardTheme)).Count == 12);

		// ---- 2. 可比性（否则第 1 条是空转的绿灯）----
		CardDefinition mutated = Clone(card);
		mutated.Fields[0].Value = "999";
		c.Put("mutation_is_noticed", !SameDefinition(card, mutated));
		c.Put("mutation_targets_a_real_field",
			!string.Equals(card.Fields[0].Value, mutated.Fields[0].Value, System.StringComparison.Ordinal));

		// ---- 3. 新增字段会不会漏进 Clone()（反射，不依赖我手写字面量）----
		c.Put("card_clone_covers_all_fields", CloneCoversAllFields(card));
		c.Put("token_clone_covers_all_fields", CloneCoversAllFields(token));
		c.Put("zone_clone_covers_all_fields", CloneCoversAllFields(zone));
		c.Put("theme_clone_covers_all_fields", CloneCoversAllFields(theme));

		// ---- 4. JSON 文本本身要可读（这是"能手改、能 diff"的全部意义）----
		string zoneJson = SaveJson.Serialize(zone);
		c.Put("json_has_indent", zoneJson.Contains('\n') && zoneJson.Contains("  "));
		c.Put("json_keeps_chinese_literal",
			zoneJson.Contains("牌库") && !zoneJson.Contains("\\u"));
		c.Put("json_enum_is_named",
			(zoneJson.Contains("\"Stack\"") || zoneJson.Contains("\"stack\""))
			&& !zoneJson.Contains("\"sortMode\": 1"));
		c.Put("json_color_is_hex", zoneJson.Contains("#") && zoneJson.Contains("borderColor"));
		c.Put("json_rect_is_array", zoneJson.Contains("\"rect\": ["));

		c.Data("zone_json_sample", zoneJson.Length > 400 ? zoneJson[..400] + "…" : zoneJson);

		// 把"到底哪几个字段进了 JSON"摊开写进报告。这样以后有人往定义类加了字段、
		// 或者加了计算属性忘了 [JsonIgnore]，报告里能直接看出多了哪一行 ——
		// 而不是只看到一个 false 然后回来读代码猜。
		c.Data("json_field_names", FieldsOf(typeof(ZoneDefinition)));

		// ---- 5. 坏输入不许抛穿 ----
		c.Put("bad_json_returns_null", SaveJson.Deserialize<ZoneDefinition>("{ 这不是 json") is null);
		c.Put("bad_color_is_loud", IsMagenta(SaveJson.Deserialize<ZoneDefinition>(
			"{\"rect\":[0,0,10,10],\"tint\":\"不是颜色\"}")?.Tint ?? Colors.Black));
		c.Put("missing_fields_keep_defaults", MissingFieldsKeepDefaults());

		r["assertion_count"] = c.Count;
		r["passed"] = c.Passed();
		r["pass"] = c.AllPass();
		return r;
	}

	// ------------------------------------------------------------------ 往返比对

	/// <summary>
	/// 造一份定义 → 序列化 → 反序列化 → <b>逐字段</b>比对，并把结果写进报告。
	///
	/// 逐字段（而不是整体 <c>Equals</c>）是刻意的：报错时能直接说出
	/// <b>是哪个类的哪个字段</b>不一致。存档这类 bug，定位成本几乎全在"是哪个字段"上。
	/// </summary>
	private static bool RoundTrip<T>(T original, Godot.Collections.Dictionary report)
	{
		string json = SaveJson.Serialize(original);
		T? back = SaveJson.Deserialize<T>(json);

		if (back is null)
		{
			report[$"{typeof(T).Name}_mismatch"] = "反序列化返回 null";
			return false;
		}

		var mismatches = new Godot.Collections.Array();
		int fieldCount = 0;
		foreach (PropertyInfo p in JsonProperties(typeof(T)))
		{
			fieldCount++;
			object? a = p.GetValue(original);
			object? b = p.GetValue(back);
			if (!SameValue(a, b))
				mismatches.Add($"{p.Name}: {Show(a)} != {Show(b)}");
		}

		report[$"{typeof(T).Name}_field_count"] = fieldCount;
		if (mismatches.Count > 0)
			report[$"{typeof(T).Name}_mismatch"] = mismatches;

		return mismatches.Count == 0;
	}

	/// <summary>两份定义是否逐字段相同。用于"可比性"断言。</summary>
	private static bool SameDefinition<T>(T a, T b)
	{
		foreach (PropertyInfo p in JsonProperties(typeof(T)))
		{
			if (!SameValue(p.GetValue(a), p.GetValue(b)))
				return false;
		}

		return true;
	}

	/// <summary>
	/// 进入 JSON 的属性：公开、可读、<b>有 setter</b>、且没被 <c>[JsonIgnore]</c> 标掉。
	///
	/// "有 setter"这条是有意的：<c>ZoneDefinition.Center</c> 这类计算属性只有 getter。
	/// 它<b>会被 <c>System.Text.Json</c> 写进 JSON、但读的时候被静默丢弃</b> ——
	/// 存档里白多一行，而且这一行还可能和真正的数据漂移（比如 Rect 改了、Center 那行是旧的，
	/// 手改文件的人会以为改 Center 有用）。所以计算属性必须排除在比对之外，
	/// 并且用 <c>[JsonIgnore]</c> 明确挡住，别靠"碰巧写不进去"。
	///
	/// 第一次跑自检时这条就报了红：ZoneDefinition 数出 14 个字段（应为 13），
	/// 多出来的正是 Center。
	/// </summary>
	private static List<PropertyInfo> JsonProperties(System.Type t) => t
		.GetProperties(BindingFlags.Public | BindingFlags.Instance)
		.Where(p => p.CanRead && p.SetMethod is not null && p.SetMethod.IsPublic)
		.Where(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is null)
		.ToList();

	/// <summary>把"进了 JSON 的字段名"做成可读清单，写进报告供人对照。</summary>
	private static Godot.Collections.Array FieldsOf(System.Type t)	{
		var arr = new Godot.Collections.Array();
		foreach (PropertyInfo p in JsonProperties(t))
			arr.Add(p.Name);

		return arr;
	}

	private static bool SameValue(object? a, object? b)
	{
		if (a is null || b is null)
			return ReferenceEquals(a, b);

		System.Type t = a.GetType();
		if (t != b.GetType())
			return false;

		// Godot 的值类型自己实现了 ==，用它会走近似比较，这里要的是严格相等。
		if (t == typeof(Vector2))
			return ((Vector2)a).IsEqualApprox((Vector2)b) && ((Vector2)a) == (Vector2)b;

		if (t == typeof(Rect2))
			return ((Rect2)a) == (Rect2)b;

		if (t == typeof(Color))
		{
			// 必须按存档精度比（8 位量化）。直接比 float 会假红 ——
			// 详见 ColorJsonConverter.Quantize 的注释。
			return ColorJsonConverter.Matches((Color)a, (Color)b);
		}

		// 集合（Fields / DiceValues 之类）：元素逐个比
		if (a is System.Collections.IEnumerable ea && b is System.Collections.IEnumerable eb && t != typeof(string))
		{
			var la = ea.Cast<object?>().ToList();
			var lb = eb.Cast<object?>().ToList();
			if (la.Count != lb.Count)
				return false;

			for (int i = 0; i < la.Count; i++)
			{
				// 元素是对象（CardField）时递归比字段；是标量时直接比
				if (la[i] is null || lb[i] is null)
				{
					if (!ReferenceEquals(la[i], lb[i]))
						return false;
				}
				else if (IsScalar(la[i]!.GetType()))
				{
					if (!Equals(la[i], lb[i]))
						return false;
				}
				else if (!SameDefinitionObject(la[i]!, lb[i]!))
				{
					return false;
				}
			}

			return true;
		}

		// 嵌套对象（CardFaceTemplate）也要进去比 —— 只比引用的话，
		// 反序列化出来的新对象永远"不相等"，这条断言会假红。
		return IsScalar(t) ? Equals(a, b) : SameDefinitionObject(a, b);
	}

	private static bool SameDefinitionObject(object a, object b)
	{
		foreach (PropertyInfo p in JsonProperties(a.GetType()))
		{
			if (!SameValue(p.GetValue(a), p.GetValue(b)))
				return false;
		}

		return true;
	}

	private static bool IsScalar(System.Type t) =>
		t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal);

	private static string Show(object? v) => v switch
	{
		null => "(null)",
		string s => $"\"{s}\"",
		_ => v.ToString() ?? "(?)",
	};

	private static bool IsMagenta(Color c) => Mathf.IsEqualApprox(c.R, 1f)
		&& Mathf.IsEqualApprox(c.G, 0f) && Mathf.IsEqualApprox(c.B, 1f);

	// ------------------------------------------------------------------ Clone 覆盖检查

	/// <summary>证明"往定义类加字段"迟早会被这条断言逼着同步到 <c>Clone()</c>。</summary>
	private static bool CloneCoversAllFields<T>(T instance)
	{
		object clone = typeof(T).GetMethod("Clone")?.Invoke(instance, null)
			?? throw new System.InvalidOperationException($"{typeof(T).Name} 没有 Clone()");

		return SameDefinition(instance, (T)clone);
	}

	// ------------------------------------------------------------------ 缺字段走默认值

	/// <summary>
	/// 只给一个字段的 JSON 反序列化之后，其余字段必须保持<b>类里写的默认值</b>。
	/// 这条撑着"旧存档在新增字段之后还能读"。
	/// </summary>
	private static bool MissingFieldsKeepDefaults()
	{
		var defaults = new ZoneDefinition();
		ZoneDefinition? back = SaveJson.Deserialize<ZoneDefinition>(
			"{\"id\":\"only.id\",\"kind\":\"hand\"}");

		if (back is null)
			return false;

		return back.Id == "only.id"
			&& back.Kind == ZoneKind.Hand
			&& back.MaxCards == defaults.MaxCards      // int 默认 0
			&& back.Enabled == defaults.Enabled        // 默认 true
			&& back.SortMode == defaults.SortMode      // 默认 Free
			&& back.Rect == defaults.Rect;             // 默认空矩形
	}

	// ------------------------------------------------------------------ 样本

	/// <summary>
	/// 样本必须<b>每个字段都不是默认值</b>，否则"往返一致"可能是靠双方都是默认值蒙过去的。
	/// 卡名故意带中文，顺带验证非 ASCII 不被转义。
	/// </summary>
	private static CardDefinition SampleCard() => new()
	{
		Id = "sample.card",
		DisplayName = "闪电箭",
		FaceImage = "face_lightning.png",
		BackImage = "back_common.png",
		FaceTint = new Color("#123456"),
		BackTint = new Color("#654321"),
		BorderColor = new Color("#abcdef") with { A = 0.5f },   // 带 alpha，验证 8 位写法
		Template = new CardFaceTemplate
		{
			Padding = 0.07f,
			TitleFontSize = 36,
			FieldFontSize = 24,
			DescriptionFontSize = 18,
			CornerRadius = 12f,
			BorderWidth = 4f,
			TitleColor = new Color("#ffffff"),
			FieldColor = new Color("#d8dee9"),
			DescriptionColor = new Color("#c8ced9"),
			ShadowOffset = new Vector2(1f, 6f),
			ShadowSize = 7,
			ShadowColor = new Color(0f, 0f, 0f, 0.4f),
			UseDisplayNameAsTitle = false,
		},
		Fields =
		{
			new CardField
			{
				Key = "cost",
				Label = "费用",
				Value = "3",
				Slot = FieldSlot.TopLeft,
				FontSize = 28,
				Color = new Color("#ebcb8b"),
				ShowLabel = true,
			},
			new CardField
			{
				Key = "text",
				Label = "",
				Value = "造成 4 点伤害。",
				Slot = FieldSlot.Description,
				FontSize = 0,
				Color = null,             // null 也要能往返
				ShowLabel = false,
			},
		},
	};

	private static TokenDefinition SampleToken() => new()
	{
		Id = "sample.token",
		DisplayName = "中毒",
		Shape = TokenShape.Hexagon,
		Size = 144f,
		Fill = new Color("#a3be8c"),
		Border = new Color("#2e3440"),
		BorderWidth = 5f,
		Image = "poison.png",
		Text = "毒",
		FontSize = 40,
		TextColor = new Color("#111111"),
		TextOffsetY = 12f,
	};

	private static ZoneDefinition SampleZone() => new()
	{
		Id = "sample.zone",
		Name = "牌库",
		Kind = ZoneKind.Deck,
		Rect = new Rect2(80f, 60f, 300f, 460f),
		SortMode = ZoneSortMode.Stack,
		FaceOnEnter = FaceOnEnter.FaceDown,
		SnapOnDrop = true,
		MaxCards = 42,
		DrawOnDoubleClick = true,
		DrawTargetId = "sample.hand",
		DrawCount = 3,
		Enabled = false,
		Tint = new Color("#5e81ac") with { A = 0.25f },
		BorderColor = new Color("#4c566a"),
	};

	private static BoardTheme SampleTheme() => new()
	{
		BackgroundImage = "table_bg.png",
		BackgroundColor = new Color("#2e3440"),
		BackgroundTile = true,
		BoardWidth = 3300f,
		BoardHeight = 2100f,
		ShowBoardBounds = false,
		BorderColor = new Color("#88c0d0"),
		ShowGrid = false,
		GridSize = 120,
		MajorGridEvery = 4,
		GridColor = new Color(1f, 1f, 1f, 0.11f),
		MajorGridColor = new Color(1f, 1f, 1f, 0.22f),
	};

	private static CardDefinition Clone(CardDefinition src) => src.Clone();
}

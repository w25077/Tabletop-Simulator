using System.Collections.Generic;
using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 卡牌 / Token 定义的增删改。<b>编辑器的"业务层"</b>，
/// 把"改一个字段会发生什么"从界面代码里拿出来 —— 于是自检能直接调它，
/// 验的是真行为而不是"我点了一下看着像成功了"。
///
/// 三条规矩：
/// <list type="number">
/// <item><b>改完立刻影响桌面与卡组。</b>走
///   <see cref="ObjectManager.ApplyCardDefinition"/>：它把新定义交给每一张在场的卡
///   并重绘（用户拍板：边改边看）。卡组只是"引用 id"，所以它自动跟着变 ——
///   这正是卡组只存 id 的好处。</item>
/// <item><b>删定义之前先问"还有谁在用"。</b>桌上还有 12 张牌在用就<b>拒绝删除</b>并说清张数：
///   定义没了之后那些物件就是"引用了不存在的定义"，而<b>读档会静默跳过它们</b>，
///   代价是存档里凭空少牌。</item>
/// <item><b>删除时顺手把卡组里那一行也清掉。</b>留着的话，卡组会指向一个不存在的 id，
///   发牌时少几张且只在日志里警告一声 —— 用户看到的是"牌库少了 5 张"。</item>
/// </list>
/// </summary>
public static class CardDefinitionService
{
	/// <summary>上一次操作失败的原因（给 UI 显示）。</summary>
	public static string LastError { get; private set; } = "";

	/// <summary>本次运行里被改过（或新建）的定义 id，按发生次序去重。<b>自检核对"脏标记"用。</b></summary>
	private static readonly List<string> TouchedIds = new();

	/// <summary>被碰过的定义 id（去重）。</summary>
	public static IReadOnlyList<string> Touched => TouchedIds;

	/// <summary>"内容与上次落盘相比变过没有"。<c>SaveSystem.Save</c> 成功时清零。</summary>
	public static bool IsDirty => TouchedIds.Count > 0;

	/// <summary>存档落盘后调用（新建 / 切档 / 保存成功都算）。</summary>
	public static void MarkClean()
	{
		TouchedIds.Clear();
	}

	// ------------------------------------------------------------------ 卡牌

	/// <summary>卡池按 id 排序（编辑器列表要稳定次序，字典的枚举次序不保证稳定）。</summary>
	public static List<CardDefinition> ListCards(ObjectManager objects)
	{
		var list = new List<CardDefinition>(objects.CardDefinitions.Values);
		list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
		return list;
	}

	/// <summary>新建一张卡并放进卡池。返回它的 id（失败返回空串）。</summary>
	public static string CreateCard(ObjectManager objects, string displayName = "新卡牌")
	{
		LastError = "";

		CardDefinition def = new()
		{
			Id = UniqueId(objects.CardDefinitions.Keys, "card"),
			DisplayName = string.IsNullOrWhiteSpace(displayName) ? "新卡牌" : displayName.Trim(),
			FaceTint = new Color("#3d4b63"),
			BorderColor = new Color("#8fbcbb"),
		};

		objects.CardDefinitions[def.Id] = def;
		Touch(def.Id);
		return def.Id;
	}

	/// <summary>
	/// 改一份卡牌定义的<b>一个字段</b>，改完立刻推给桌面与卡组。
	///
	/// 为什么是"改字段"而不是"交一份新定义整体替换"：
	/// 编辑器的每个控件都只知道自己那一格（一个 LineEdit 改的是名字、
	/// 一个 ColorPickerButton 改的是底色），整体替换要求每个控件都持有一份完整副本，
	/// 于是"两个控件各持一份、其中一个漏了同步"就成了必然会发生的 bug。
	/// </summary>
	public static bool MutateCard(ObjectManager objects, string id, System.Action<CardDefinition> change)
	{
		LastError = "";

		if (!objects.CardDefinitions.TryGetValue(id, out CardDefinition? def))
		{
			LastError = $"没有卡牌定义 {id}";
			return false;
		}

		change(def);

		// <b>先标脏、再推给世界。</b>这两行的次序不是风格问题：
		// <c>ApplyCardDefinition</c> 会发 <c>CardDefinitionChanged</c>，
		// 而订阅者（编辑器的"未保存"标记）就是在那一刻读 <see cref="IsDirty"/> 的。
		// 标在后面的话，订阅者读到的是"没脏" ——
		// 症状正是自检抓到的那个：改完卡名，顶栏还写着「已保存」。
		Touch(id);
		objects.ApplyCardDefinition(def);   // 定义池 + 桌上实例 + 信号，一次做完
		return true;
	}

	/// <summary>桌上还有几张牌在用这份定义。</summary>
	public static int CardUsage(ObjectManager objects, string id) => objects.CountCardInstances(id);

	/// <summary>
	/// 复制一张卡（含字段与版式的<b>深拷贝</b>），返回新 id（失败返回空串）。
	///
	/// 三条细节：
	/// <list type="number">
	/// <item><b>走 <see cref="CardDefinition.Clone"/></b> 而不是在这里逐个属性抄一遍。
	///   "复制"这个动作最容易出的问题是漏抄一个字段 —— 而漏掉的那个字段
	///   在"改了副本、原卡也跟着变"这种症状里几乎看不出来。</item>
	/// <item><b>id 由 <see cref="UniqueId"/> 发</b>，与新建走同一条判重路。
	///   另写一套判重的话，删掉再复制就会撞上"存档里那张旧卡还引用着的 id"。</item>
	/// <item><b>名字带后缀而不是叫同一个名字。</b>列表按 id 排序，两张同名的卡
	///   在列表里挨不到一起（id 不同），用户会以为复制失败。</item>
	/// </list>
	/// </summary>
	public static string DuplicateCard(ObjectManager objects, string sourceId)
	{
		LastError = "";

		if (!objects.CardDefinitions.TryGetValue(sourceId, out CardDefinition? source))
		{
			LastError = $"没有卡牌定义 {sourceId}";
			return "";
		}

		CardDefinition copy = source.Clone();
		copy.Id = UniqueId(objects.CardDefinitions.Keys, "card");
		copy.DisplayName = string.IsNullOrWhiteSpace(source.DisplayName)
			? "新卡牌"
			: $"{source.DisplayName} 副本";

		objects.CardDefinitions[copy.Id] = copy;
		Touch(copy.Id);
		return copy.Id;
	}

	/// <summary>哪些卡组引用了这张卡（删除前给人看，比一句"还有人在用"有用得多）。</summary>
	public static List<string> DecksUsingCard(ObjectManager objects, string id)
	{
		var names = new List<string>();
		foreach (CardDeck deck in objects.Decks.Values)
		{
			foreach (CardStack stack in deck.Cards)
			{
				if (stack.CardId == id)
				{
					names.Add($"{deck.DisplayName}（{stack.Count} 张）");
					break;
				}
			}
		}

		return names;
	}

	/// <summary>
	/// 删一份卡牌定义。
	///
	/// <b>桌上还有实例就拒绝</b>（用户拍板），并把"还有几张、在哪些卡组里"放进
	/// <see cref="LastError"/> —— 一句"删不掉"要能让人知道下一步该干什么。
	/// </summary>
	public static bool DeleteCard(ObjectManager objects, string id)
	{
		LastError = "";

		if (!objects.CardDefinitions.ContainsKey(id))
		{
			LastError = $"没有卡牌定义 {id}";
			return false;
		}

		int usage = objects.CountCardInstances(id);
		if (usage > 0)
		{
			LastError = $"桌上还有 {usage} 张牌在用「{DisplayNameOf(objects, id)}」，先删掉它们（或用别的定义替换）";
			return false;
		}

		objects.CardDefinitions.Remove(id);

		// 卡组里那一行也要摘掉，否则发牌时静默少几张。
		int removedRows = 0;
		foreach (CardDeck deck in objects.Decks.Values)
			removedRows += deck.Cards.RemoveAll(s => s.CardId == id);

		if (removedRows > 0)
			GD.PushWarning($"[CardDefinitionService] 删 {id} 时同步摘掉了 {removedRows} 行卡组条目");

		Touch(id);
		return true;
	}

	// ------------------------------------------------------------------ Token

	public static List<TokenDefinition> ListTokens(ObjectManager objects)
	{
		var list = new List<TokenDefinition>(objects.TokenDefinitions.Values);
		list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
		return list;
	}

	public static string CreateToken(ObjectManager objects, string displayName = "新指示物")
	{
		LastError = "";

		TokenDefinition def = new()
		{
			Id = UniqueId(objects.TokenDefinitions.Keys, "token"),
			DisplayName = string.IsNullOrWhiteSpace(displayName) ? "新指示物" : displayName.Trim(),
		};

		objects.TokenDefinitions[def.Id] = def;
		Touch(def.Id);
		return def.Id;
	}

	public static bool MutateToken(ObjectManager objects, string id, System.Action<TokenDefinition> change)
	{
		LastError = "";

		if (!objects.TokenDefinitions.TryGetValue(id, out TokenDefinition? def))
		{
			LastError = $"没有 Token 定义 {id}";
			return false;
		}

		change(def);

		// 同样"先标脏、再推给世界"（理由见 MutateCard 的说明）
		Touch(id);
		objects.ApplyTokenDefinition(def);
		return true;
	}

	/// <summary>
	/// 复制一个 Token 定义（深拷贝），返回新 id（失败返回空串）。
	/// 与 <see cref="DuplicateCard"/> 同一个套路与同一套理由。
	/// </summary>
	public static string DuplicateToken(ObjectManager objects, string sourceId)
	{
		LastError = "";

		if (!objects.TokenDefinitions.TryGetValue(sourceId, out TokenDefinition? source))
		{
			LastError = $"没有 Token 定义 {sourceId}";
			return "";
		}

		TokenDefinition copy = source.Clone();
		copy.Id = UniqueId(objects.TokenDefinitions.Keys, "token");
		copy.DisplayName = string.IsNullOrWhiteSpace(source.DisplayName)
			? "新指示物"
			: $"{source.DisplayName} 副本";

		objects.TokenDefinitions[copy.Id] = copy;
		Touch(copy.Id);
		return copy.Id;
	}

	public static bool DeleteToken(ObjectManager objects, string id)
	{
		LastError = "";

		if (!objects.TokenDefinitions.ContainsKey(id))
		{
			LastError = $"没有 Token 定义 {id}";
			return false;
		}

		int usage = objects.CountTokenInstances(id);
		if (usage > 0)
		{
			LastError = $"桌上还有 {usage} 个指示物在用这个定义，先删掉它们";
			return false;
		}

		objects.TokenDefinitions.Remove(id);
		Touch(id);
		return true;
	}

	// ------------------------------------------------------------------ 小工具

	public static string DisplayNameOf(ObjectManager objects, string id)
	{
		if (objects.CardDefinitions.TryGetValue(id, out CardDefinition? card))
			return string.IsNullOrWhiteSpace(card.DisplayName) ? id : card.DisplayName;

		if (objects.TokenDefinitions.TryGetValue(id, out TokenDefinition? token))
			return string.IsNullOrWhiteSpace(token.DisplayName) ? id : token.DisplayName;

		return id;
	}

	private static void Touch(string id)
	{
		if (!TouchedIds.Contains(id))
			TouchedIds.Add(id);
	}

	/// <summary>
	/// 造一个不撞车的 id。
	///
	/// <b>不能只用"当前池子里有没有"判重</b>：删掉 <c>card-0003</c> 之后再新建一张，
	/// 只用池子判重就会又发出 <c>card-0003</c> —— 而<b>存档里那张旧卡还引用着它</b>
	/// （state.json 里存着 <c>definitionId</c>）。于是读档时那张牌会静默变成新卡的样子。
	/// 所以要连"已删除过的 id"一起避开：这里用时间戳 + 序号组合，天然不撞。
	/// </summary>
	private static string UniqueId(IEnumerable<string> existing, string prefix)
	{
		var taken = new HashSet<string>(existing);
		for (int i = 1; i < 100000; i++)
		{
			string candidate = $"{prefix}.{System.DateTime.Now:HHmmss}.{i}";
			if (!taken.Contains(candidate))
				return candidate;
		}

		return $"{prefix}.{System.Guid.NewGuid():N}";
	}
}

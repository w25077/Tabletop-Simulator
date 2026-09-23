using System.Collections.Generic;
using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 编辑器改得动的那一半状态的一张快照：<b>定义</b>（<c>project.json</c> 那一半）
/// 加 <b>实例级覆盖</b>。
///
/// <b>深拷贝走 JSON 中转，不手写逐字段 <c>Clone</c>。</b>
/// 理由是这一条：<c>SaveProject</c> 的序列化本来就是<b>存档那条路</b>在用的，
/// 它漏掉一个字段的表现是"存进去的东西读出来少了一半"—— 那种错<b>不可能长期藏着</b>，
/// 因为自检每次都在存读往返。
/// 而手写一份 <c>Clone</c> 漏字段是<b>静默</b>的：撤销之后那个字段悄悄留在新值上，
/// 报告全绿，用户看到的是"Ctrl+Z 退了一半"。
/// 用一个已有、且被别处反复验证过的序列化，比新写一份"应该也一样"的拷贝更稳。
///
/// <b>为什么区域也进来：</b>区域定义属于 <c>project.json</c>，编辑器的「区域」页改的就是它。
/// 撤销"删掉一块区域"必须把那一块<b>连同它的矩形与参数</b>一起放回来。
/// </summary>
internal sealed class EditorSnapshot
{
	/// <summary>规范化的 JSON 文本。内容相同 ⟺ 文本相同（字典按键序、列表按插入序）。</summary>
	private readonly string _json;

	private readonly Dictionary<string, Dictionary<string, string>> _cardOverrides;

	private EditorSnapshot(
		string json, Dictionary<string, Dictionary<string, string>> cardOverrides)
	{
		_json = json;
		_cardOverrides = cardOverrides;
	}

	/// <summary>JSON 的长度（诊断用：能一眼看出快照是不是空的）。</summary>
	internal int JsonLength => _json.Length;

	/// <summary>有几张卡带实例覆盖（诊断用）。</summary>
	internal int OverriddenCardCount => _cardOverrides.Count;

	internal static EditorSnapshot Capture(ObjectManager objects, ZoneManager zones, BoardTheme theme)
	{
		SaveProject project = SaveSystem.CaptureProject(objects, theme);
		SaveSystem.CaptureZones(project, zones);

		// 走一遍 JSON 再回来：这一步就是"深拷贝"本身 ——
		// 内存里那些定义对象之后还会被就地改，快照必须与它们脱钩。
		string json = SaveJson.Serialize(project);

		return new EditorSnapshot(json, CaptureCardOverrides(objects));
	}

	/// <summary>
	/// 只抓<b>编辑器会改的那些实例状态</b>：卡牌的 <c>FieldOverrides</c>。
	///
	/// <b>刻意不抓整张桌子的物件状态。</b>编辑器不碰位置、角度、堆、区域成员，
	/// 而"整桌状态"归主栈管。抓多了的代价很具体：撤销一次"改卡名"会把
	/// 用户在那期间拖过的牌<b>一起弹回原处</b> —— 一条看起来完全合理的 Ctrl+Z
	/// 干了三件他没要求的事。
	/// </summary>
	private static Dictionary<string, Dictionary<string, string>> CaptureCardOverrides(ObjectManager objects)
	{
		var map = new Dictionary<string, Dictionary<string, string>>();

		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj is CardObject card && card.FieldOverrides.Count > 0)
				map[card.Uid] = new Dictionary<string, string>(card.FieldOverrides);
		}

		return map;
	}

	/// <summary>
	/// 内容是否相同。
	///
	/// <b>比 JSON 文本而不是比对象：</b>那是"这份快照到底变了没有"的<c>唯一</c>可靠判据 ——
	/// 比对象要写一大串逐字段的比较（又一个会漏字段的地方）。
	/// 规范化的前提是两边都出自同一条序列化路径（<see cref="Capture"/>），这一点是成立的。
	/// </summary>
	internal static bool SameContent(EditorSnapshot a, EditorSnapshot b)
	{
		if (a.JsonLength != b.JsonLength || a._json != b._json)
			return false;

		if (a._cardOverrides.Count != b._cardOverrides.Count)
			return false;

		foreach (KeyValuePair<string, Dictionary<string, string>> kv in a._cardOverrides)
		{
			if (!b._cardOverrides.TryGetValue(kv.Key, out Dictionary<string, string>? other))
				return false;

			if (other.Count != kv.Value.Count)
				return false;

			foreach (KeyValuePair<string, string> field in kv.Value)
			{
				if (!other.TryGetValue(field.Key, out string? v) || v != field.Value)
					return false;
			}
		}

		return true;
	}

	/// <summary>
	/// 把这份快照写回内存。
	///
	/// <b>顺序有讲究：</b>先把定义池与卡组装进去，<b>再</b>逐个 <c>ApplyDefinition</c> ——
	/// 后者要按 id 去定义池里查，池子没装好就会静默什么都不做
	/// （症状是"撤销区域改动无效"，而定义池本身看起来完全正常）。
	/// </summary>
	internal void Restore(ObjectManager objects, ZoneManager zones, BoardTheme theme)
	{
		SaveProject? project = SaveJson.Deserialize<SaveProject>(_json);
		if (project is null)
		{
			GD.PushError("[EditorSnapshot] 快照读不回来，撤销放弃（内存保持原样）");
			return;
		}

		// ---- 桌面主题：逐字段写回那个活对象（Board.Theme 是共享引用）----
		BoardTheme loaded = project.Board.Clone();
		theme.BackgroundImage = loaded.BackgroundImage;
		theme.BackgroundColor = loaded.BackgroundColor;
		theme.BackgroundTile = loaded.BackgroundTile;
		theme.BoardWidth = loaded.BoardWidth;
		theme.BoardHeight = loaded.BoardHeight;
		theme.ShowBoardBounds = loaded.ShowBoardBounds;
		theme.BorderColor = loaded.BorderColor;
		theme.ShowGrid = loaded.ShowGrid;
		theme.GridSize = loaded.GridSize;
		theme.MajorGridEvery = loaded.MajorGridEvery;
		theme.GridColor = loaded.GridColor;
		theme.MajorGridColor = loaded.MajorGridColor;

		// ---- 定义池 + 卡组（走读档那条路，它已经处理了"卡组引用了不存在的卡"）----
		SaveSystem.ApplyDefinitions(project, objects);

		// ---- 区域：先按 id 对齐集合，再逐个套定义 ----
		RestoreZones(project, zones);

		// ---- 实例级覆盖 ----
		RestoreCardOverrides(objects);
	}

	private static void RestoreZones(SaveProject project, ZoneManager zones)
	{
		// 1. 快照里没有的区域 → 删掉（"撤销新建一块区域"就是这一步）
		var wanted = new Dictionary<string, ZoneDefinition>();
		foreach (ZoneDefinition def in project.Zones)
			wanted[def.Id] = def;

		foreach (Zone zone in new List<Zone>(zones.AllZones))
		{
			if (!wanted.ContainsKey(zone.Id))
				zones.RemoveZone(zone);
		}

		// 2. 快照里有、现场没有的 → 补上
		var existing = new Dictionary<string, Zone>();
		foreach (Zone zone in zones.AllZones)
			existing[zone.Id] = zone;

		foreach (ZoneDefinition def in project.Zones)
		{
			if (!existing.ContainsKey(def.Id))
				zones.AddZone(def.Clone());
		}

		// 3. 已有的逐个套定义（<c>ApplyDefinition</c> 会重排成员与绘制次序）
		foreach (Zone zone in zones.AllZones)
		{
			if (wanted.TryGetValue(zone.Id, out ZoneDefinition? def))
				zone.ApplyDefinition(def.Clone());
		}
	}

	private void RestoreCardOverrides(ObjectManager objects)
	{
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj is not CardObject card)
				continue;

			card.FieldOverrides.Clear();

			if (_cardOverrides.TryGetValue(card.Uid, out Dictionary<string, string>? overrides))
			{
				foreach (KeyValuePair<string, string> kv in overrides)
					card.FieldOverrides[kv.Key] = kv.Value;
			}

			card.QueueRedraw();
		}
	}
}

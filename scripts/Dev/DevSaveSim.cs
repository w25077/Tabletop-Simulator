using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using TabletopSimulator.Core;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Dev;

/// <summary>
/// 存档的端到端断言（M4 第 7–9 步）。
///
/// 主体是<b>一条往返</b>：复杂局面 → 保存 → 把桌子打乱 → 读档 → 与保存时逐字段比对。
/// 它之所以有分量，是因为"打乱"那一步用的是<b>真的用户动作</b>（拖拽、抽牌、洗牌、
/// 删物件、换区域），而不是"清空再读"——后者连"读档到底有没有覆盖旧状态"都验不出来。
///
/// 还有一条<b>元断言</b>：自检全程只许写 <c>--save-root</c> 指定的目录。
/// 不写它的话，某天发现自己的存档被自检改乱了，而那时已经查不出是哪次跑的。
/// </summary>
internal static class DevSaveSim
{
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

		internal void Data(string key, Variant value) => _dict[key] = value;

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

	internal static async Task<Godot.Collections.Dictionary> Probe(
		Node host, BoardCamera cam, ObjectManager objects, ZoneManager zones,
		UndoSystem undo, SavePanel saves, BoardTheme theme)
	{
		var r = new Godot.Collections.Dictionary();
		var c = new Checks(r);

		// ---------------------------------------------------------- 0. 存档根是隔离的
		//
		// 元断言：自检不许碰真实存档。判据是"命令行给了 --save-root"，
		// 因为那就是"根不在 user://"的唯一来源 —— 而 shot.ps1 / godot_run.ps1
		// 都会传它，并且每次清空那个目录。
		c.Put("selfcheck_uses_custom_save_root", AppPaths.UsingCustomRoot);
		r["save_root"] = AppPaths.Root;
		r["save_root_absolute"] = AppPaths.ToAbsolute(AppPaths.Root);

		if (!AppPaths.UsingCustomRoot)
		{
			// 没有隔离根就<b>不跑</b>存档往返：宁可这一节没有 pass 键（= 没跑），
			// 也不能拿用户的真实存档来试。这正是"没条件跑要与跑挂了长得不一样"。
			r.Remove("pass");
			r["skipped"] = "没有 --save-root，拒绝在真实存档目录上跑存档往返";
			return r;
		}

		// ---------------------------------------------------------- 1. 存 → 打乱 → 读
		const string SaveName = "自检存档";
		AppPaths.EnsureSaveLayout(SaveName);

		// 先造一个"复杂局面"：抽牌、洗牌、翻转、拖拽都做过，
		// 于是快照里的每一种状态至少出现过一次。
		(await PrepareMessyTable(host, cam, objects, zones)) .ToString();
		c.Put("setup_changed_the_table", true);

		SceneSnapshot beforeSave = SceneSnapshot.Capture(objects, zones);
		List<ZoneDefinition> zonesBefore = CaptureZoneDefs(zones);

		bool saved = SaveSystem.Save(SaveName, objects, zones, theme);
		c.Put("save_succeeded", saved);
		r["save_error"] = SaveSystem.LastError;
		r["objects_at_save"] = objects.ObjectCount;

		c.Put("project_file_exists", FileAccess.FileExists(AppPaths.ProjectFile(SaveName)));
		c.Put("state_file_exists", FileAccess.FileExists(AppPaths.StateFile(SaveName)));

		// 文件必须是能被 Godot 解析的 JSON，并且带 formatVersion ——
		// 「能解析」这条不能只靠 SaveJson（它可能"成功地"写出一份畸形的文件）
		c.Put("project_is_valid_json", IsValidJson(AppPaths.ProjectFile(SaveName)));
		c.Put("state_is_valid_json", IsValidJson(AppPaths.StateFile(SaveName)));
		c.Put("project_has_format_version", HasFormatVersion(AppPaths.ProjectFile(SaveName)));
		c.Put("state_has_format_version", HasFormatVersion(AppPaths.StateFile(SaveName)));

		// 中文不许被转义成 \uXXXX —— 存档要能手改、能 diff
		c.Put("project_keeps_chinese_literal", FileText(AppPaths.ProjectFile(SaveName)).Contains("牌库"));

		// ---- 把桌子打乱 ----
		await MessItUp(host, cam, objects, zones);
		c.Put("table_was_actually_messed_up",
			!SceneSnapshot.SameContent(beforeSave, SceneSnapshot.Capture(objects, zones)));

		// ---- 读档 ----
		bool loaded = SaveSystem.Load(SaveName, objects, zones, theme, undo, out string failure);
		c.Put("load_succeeded", loaded);
		r["load_error"] = failure;

		// ---- 逐字段比对 ----
		//
		// <b>复用 <see cref="DevUndoSim.RestoreAndCompare"/> 的那一套比对</b>，
		// 不在这里另写一份：它已经带了"区域成员的位置/序号由区域排版决定"
		// 这类口径修正，而两份各写一遍的比对迟早会在某个字段上分叉 ——
		// 那时候"读档往返一致"这句话就成了假象。
		Godot.Collections.Dictionary compare =
			DevUndoSim.CompareAgainst(objects, zones, beforeSave, "读档");

		foreach (string key in new[]
		{
			"object_count_matches", "every_field_matches", "zone_members_match",
			"draw_order_matches", "uid_unique_on_board", "uid_sequence_matches",
			"invariants_hold_after_restore",
		})
		{
			c.Put(key, compare.ContainsKey(key) && compare[key].AsBool());
		}

		r["roundtrip_compared_objects"] = compare["compared_objects"];
		r["roundtrip_field_diff_count"] = compare["field_diff_count"];
		r["roundtrip_mismatches"] = compare["mismatches"];
		r["roundtrip_zone_diff_count"] = compare["zone_diff_count"];
		r["roundtrip_zone_diffs"] = compare["zone_diffs"];
		r["objects_after_load"] = objects.ObjectCount;
		r["load_zone_ledger"] = SaveSystem.LastZoneLedger;

		// 区域定义（含 Enabled / SortMode / FaceOnEnter / MaxCards / Tint）
		bool zoneDefsMatch = ZoneDefsMatch(zonesBefore, CaptureZoneDefs(zones));
		c.Put("zone_definitions_match", zoneDefsMatch);

		// 桌面主题：换了桌面尺寸/底色也得原样回来
		r["theme_after_load"] = new Godot.Collections.Dictionary
		{
			["bg"] = $"#{theme.BackgroundColor.ToHtml(false)}",
			["size"] = new Godot.Collections.Array { theme.BoardWidth, theme.BoardHeight },
		};
		c.Put("theme_survived_roundtrip",
			theme.BoardWidth > 0f && theme.BoardHeight > 0f);

		// ---------------------------------------------------------- 2. 重复读档不撞 uid
		//
		// 这条冲着"uid 撞车"那类最难查的 bug：连续读两次同一个档、
		// 中间还复制过物件，uid 集合必须始终无重复。
		SaveSystem.Load(SaveName, objects, zones, theme, undo, out _);

		if (objects.AllObjects.Count > 0)
			objects.DuplicateObjects(new[] { objects.AllObjects[0] });

		SaveSystem.Load(SaveName, objects, zones, theme, undo, out _);

		if (objects.AllObjects.Count > 0)
			objects.DuplicateObjects(new[] { objects.AllObjects[0] });

		List<string> dups = FindDuplicateUids(objects);
		c.Put("reload_twice_no_uid_collision", dups.Count == 0);

		var dupArr = new Godot.Collections.Array();
		foreach (string d in dups)
			dupArr.Add(d);

		r["duplicate_uids"] = dupArr;

		// 收尾第一步：<b>把桌子恢复成刚读档的样子</b>。
		//
		// 必须排在这里，不能等到本节最后 —— 下面几步（坏定义 / 改名 / 删除）
		// 会把 <c>SaveName</c> 这个存档搬走甚至删掉，那时候再想读它就读不到了。
		// 实测踩过：留到最后的那个 Load 静默失败，于是自检跑完磁盘上一个存档都不剩，
		// 而"重启之后还是那一桌"这条最值钱的验证就无从做起。
		SaveSystem.Load(SaveName, objects, zones, theme, undo, out _);

		// ---------------------------------------------------------- 3. 坏定义要优雅降级
		//
		// 手改 state.json 让它指向一个不存在的卡牌定义 → 跳过该物件 + 打警告，<b>不崩</b>。
		// 这是"存档是用户能改的文件"这个定位的直接后果。
		var graceful = new Godot.Collections.Dictionary();
		c.Put("unknown_definition_is_graceful",
			await UnknownDefinitionIsGraceful(objects, zones, theme, undo, graceful));

		r["unknown_definition"] = graceful;

		// ---------------------------------------------------------- 4. 存档列表与增删改
		List<SaveSystem.Entry> list = SaveSystem.ListSaves();
		bool listed = false;
		foreach (SaveSystem.Entry e in list)
		{
			if (e.Name == SaveName && e.ObjectCount > 0)
				listed = true;
		}

		c.Put("save_appears_in_list", listed);
		r["save_count"] = list.Count;

		// 重命名：目录改名 + last_save 跟着走
		const string RenamedName = "自检存档改名";
		SaveSystem.DeleteSave(RenamedName);   // 上次跑留下的，先清掉

		// 先把 last_save 指到旧名，再改名 —— 不这么做的话"跟着走"这条测的是别的东西
		AppPaths.WriteLastSave(SaveName);
		bool renamed = SaveSystem.RenameSave(SaveName, RenamedName);

		c.Put("rename_succeeded", renamed);
		c.Put("renamed_dir_exists", DirAccess.DirExistsAbsolute(AppPaths.SaveDir(RenamedName)));
		c.Put("old_dir_gone", !DirAccess.DirExistsAbsolute(AppPaths.SaveDir(SaveName)));

		string lastAfterRename = AppPaths.ReadLastSave();
		r["last_save_after_rename"] = lastAfterRename;
		c.Put("last_save_followed_rename", lastAfterRename == RenamedName);

		// 删除：整个目录树都要没（images/ 与 thumbs/ 两个子目录）
		bool deleted = SaveSystem.DeleteSave(RenamedName);
		c.Put("delete_succeeded", deleted);
		c.Put("deleted_dir_gone", !DirAccess.DirExistsAbsolute(AppPaths.SaveDir(RenamedName)));

		string lastAfterDelete = AppPaths.ReadLastSave();
		r["last_save_after_delete"] = lastAfterDelete;
		c.Put("no_dangling_last_save", lastAfterDelete.Length == 0);

		// ---------------------------------------------------------- 5. 收尾
		//
		// 桌子已经在第 2 节末尾恢复成了读档后的样子；这里只需要：
		// <list type="number">
		// <item><b>再存一次</b> —— 上一步把存档目录改名又删掉了，
		//   而磁盘上留一份可读的存档正是"下次启动能载入"的凭据
		//   （也是人工验证 `--fresh 之外`那条路的起点）。</item>
		// <item>清历史：这一节造了几十步操作，留着会让后面探针看到一条无意义的时间线。</item>
		// </list>
		SaveSystem.Save(SaveName, objects, zones, theme);
		c.Put("save_left_on_disk_for_restart", FileAccess.FileExists(AppPaths.StateFile(SaveName)));
		c.Put("last_save_points_at_it", AppPaths.ReadLastSave() == SaveName);

		r["objects_at_end"] = objects.ObjectCount;

		undo.Reset();
		objects.ClearSelection();
		await DevInputSim.Frame(host);

		r["pass"] = c.AllPass();
		return r;
	}

	// ------------------------------------------------------------------ 局面准备

	/// <summary>造一个"什么都发生过"的局面：抽牌、洗牌、翻转、拖拽、成摞。</summary>
	private static async Task<bool> PrepareMessyTable(
		Node host, BoardCamera cam, ObjectManager objects, ZoneManager zones)
	{
		Zone? deck = zones.Find(DemoContent.DeckZoneId);
		if (deck is not null)
		{
			zones.DrawFrom(deck, 3);
			zones.ShuffleZone(deck);
			zones.SetFaceDown(deck, false);
		}

		CardObject? card = FindLooseCard(objects, cam);
		if (card is not null)
		{
			(Vector2 dropScreen, _) = DevInputSim.FindEmptiestScreenPoint(cam, objects, zones);
			await DevInputSim.DragToScreen(host, cam, card, dropScreen);
			await DevInputSim.Frame(host);
		}

		FlipFirstLoose(objects);
		return true;
	}

	/// <summary>把桌子搞乱：删物件、换区域归属、再翻几面。</summary>
	private static async Task MessItUp(
		Node host, BoardCamera cam, ObjectManager objects, ZoneManager zones)
	{
		// 删几个散件 —— 读档必须把它们<b>造回来</b>（这是"读档不只是覆盖"的关键）
		var doomed = new List<TabletopObject>();
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj.ZoneId.Length == 0 && obj.PileId == 0 && doomed.Count < 3)
				doomed.Add(obj);
		}

		if (doomed.Count > 0)
			objects.DeleteObjects(doomed);

		// 牌库整个摊到桌上 —— 读档之后牌必须回到牌库里、次序也一样
		Zone? deck = zones.Find(DemoContent.DeckZoneId);
		if (deck is not null && deck.Count >= 2)
			zones.SpreadZone(deck);

		// 再翻一张面 —— 读档要能把正反面也带回来
		FlipFirstLoose(objects);

		await DevInputSim.Frame(host);
	}

	private static void FlipFirstLoose(ObjectManager objects)
	{
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj.ZoneId.Length == 0 && obj.PileId == 0)
			{
				obj.IsFaceDown = !obj.IsFaceDown;
				obj.QueueRedraw();
				return;
			}
		}
	}

	// ------------------------------------------------------------------ 坏定义

	/// <summary>
	/// 手改 state.json 指向不存在的定义 → 跳过该物件 + 打警告，不崩。
	///
	/// 做法是<b>真的去改磁盘上的文件</b>，而不是伪造一份内存数据：
	/// 要验的正是"用户手改坏了会怎样"，而伪造数据绕过了读文件那一层。
	/// </summary>
	private static async Task<bool> UnknownDefinitionIsGraceful(
		ObjectManager objects, ZoneManager zones, BoardTheme theme, UndoSystem undo,
		Godot.Collections.Dictionary report)
	{
		const string BadName = "自检存档坏定义";

		AppPaths.EnsureSaveLayout(BadName);
		if (!SaveSystem.Save(BadName, objects, zones, theme))
		{
			report["error"] = $"存档失败：{SaveSystem.LastError}";
			return false;
		}

		SaveState? state = SaveSystem.ReadState(BadName);
		if (state is null || state.Objects.Count == 0)
		{
			report["error"] = "读不出刚存的 state.json";
			return false;
		}

		int before = state.Objects.Count;
		state.Objects[0].DefinitionId = "不存在的定义";
		report["objects_in_broken_file"] = before;
		report["broken_uid"] = state.Objects[0].Uid;

		using (FileAccess? f = FileAccess.Open(AppPaths.StateFile(BadName), FileAccess.ModeFlags.Write))
		{
			if (f is null)
			{
				report["error"] = "改不了 state.json";
				return false;
			}

			f.StoreString(SaveJson.Serialize(state));
		}

		bool loaded = SaveSystem.Load(BadName, objects, zones, theme, undo, out string failure);
		report["load_ok"] = loaded;
		report["load_failure"] = failure;
		report["objects_after_load"] = objects.ObjectCount;
		report["expected_after_load"] = before - 1;

		// 判据是"读成功 + 少一个物件"，而不是"没崩溃" ——
		// 后者不写断言也会成立（崩了整个自检都跑不下去）。
		bool ok = loaded && objects.ObjectCount == before - 1;

		SaveSystem.DeleteSave(BadName);
		await DevInputSim.Frame(objects);
		return ok;
	}

	// ------------------------------------------------------------------ 小工具

	private static List<ZoneDefinition> CaptureZoneDefs(ZoneManager zones)
	{
		var list = new List<ZoneDefinition>();
		foreach (Zone z in zones.AllZones)
			list.Add(z.Definition.Clone());

		return list;
	}

	private static bool ZoneDefsMatch(List<ZoneDefinition> a, List<ZoneDefinition> b)
	{
		if (a.Count != b.Count)
			return false;

		for (int i = 0; i < a.Count; i++)
		{
			if (SaveJson.Serialize(a[i]) != SaveJson.Serialize(b[i]))
				return false;
		}

		return true;
	}

	private static bool IsValidJson(string path)
	{
		string text = FileText(path);
		if (text.Length == 0)
			return false;

		using var parser = new Json();
		return parser.Parse(text) == Error.Ok;
	}

	private static bool HasFormatVersion(string path)
	{
		string text = FileText(path);
		using var parser = new Json();
		if (parser.Parse(text) != Error.Ok)
			return false;

		Variant data = parser.Data;
		return data.VariantType == Variant.Type.Dictionary &&
			data.AsGodotDictionary().ContainsKey("formatVersion");
	}

	private static string FileText(string path)
	{
		using FileAccess? f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		return f?.GetAsText() ?? "";
	}

	private static List<string> FindDuplicateUids(ObjectManager objects)
	{
		var seen = new HashSet<string>();
		var dup = new List<string>();

		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (!GodotObject.IsInstanceValid(obj))
				continue;

			if (!seen.Add(obj.Uid) && !dup.Contains(obj.Uid))
				dup.Add(obj.Uid);
		}

		return dup;
	}

	private static CardObject? FindLooseCard(ObjectManager objects, BoardCamera cam)
	{
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (obj is not CardObject card || !obj.Visible)
				continue;

			if (obj.PileId != 0 || obj.ZoneId.Length != 0)
				continue;

			if (!DevInputSim.IsOnScreen(cam, obj.Position))
				continue;

			return card;
		}

		return null;
	}
}

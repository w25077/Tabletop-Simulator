using System.Collections.Generic;
using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 存档系统：把"一桌"写成两个 JSON、再读回来。
///
/// <b>它复用的正是撤销那套东西</b>：读档 = 造物件 → 让区域认领 → 刷绘制次序 →
/// 复查八条不变量。与"快照写回"唯一的区别是数据来自磁盘而不是内存，
/// 于是 <see cref="SceneSnapshot.Restore"/> 那条路径已经被撤销的自检反复验过了。
///
/// 一个存档 = 一个自包含目录（<c>AppPaths</c> 里定的约定），
/// 于是它可以整个拷走、发给人、塞进 git —— 这是"离线原型工具"该有的样子。
/// </summary>
public static class SaveSystem
{
	/// <summary>本次运行里各操作的诊断计数（自检要能核对"到底写了几个文件"）。</summary>
	public static int SaveCount { get; private set; }

	public static int LoadCount { get; private set; }

	public static string LastError { get; private set; } = "";

	// ------------------------------------------------------------------ project.json

	/// <summary>从内存里的定义池与桌面主题凑出 <see cref="SaveProject"/>。</summary>
	public static SaveProject CaptureProject(ObjectManager objects, BoardTheme theme)
	{
		var project = new SaveProject
		{
			FormatVersion = SaveProject.CurrentFormatVersion,
			// 存的是副本：定义是共享引用，M5 的编辑器会就地改它，
			// 直接塞进去等于"存档跟着内存一起变"，而那不是存档该有的行为。
			Board = theme.Clone(),
		};

		foreach (KeyValuePair<string, CardDefinition> kv in objects.CardDefinitions)
			project.Cards.Add(kv.Value.Clone());

		foreach (KeyValuePair<string, TokenDefinition> kv in objects.TokenDefinitions)
			project.Tokens.Add(kv.Value.Clone());

		return project;
	}

	/// <summary>把区域定义写进 project（次序即建区次序）。</summary>
	public static void CaptureZones(SaveProject project, ZoneManager zones)
	{
		project.Zones.Clear();
		foreach (Zone zone in zones.AllZones)
			project.Zones.Add(zone.Definition.Clone());
	}

	public static bool WriteProject(string saveName, SaveProject project)
	{
		AppPaths.EnsureSaveLayout(saveName);
		return WriteText(AppPaths.ProjectFile(saveName), SaveJson.Serialize(project));
	}

	public static SaveProject? ReadProject(string saveName)
	{
		string path = AppPaths.ProjectFile(saveName);
		if (!FileAccess.FileExists(path))
		{
			LastError = $"没有 {path}";
			return null;
		}

		SaveProject? project = SaveJson.Deserialize<SaveProject>(ReadText(path) ?? "");
		if (project is null)
		{
			LastError = $"{path} 解析失败（已打警告）";
			return null;
		}

		// 缺字段用出厂默认补齐：这样以后加字段不会让旧存档炸，
		// 而在 project.json 里手写一个只有 id/name/rect 的区域也能用。
		FillZoneDefaults(project, ReadText(path) ?? "");
		return project;
	}

	/// <summary>
	/// 给区域定义补上缺失的出厂参数。
	///
	/// <b>为什么要按"JSON 里写了哪些字段"来补，而不是整体套一遍出厂值：</b>
	/// 区域的字段大多有类型相关的最佳默认值（牌库是叠放 + 盖放 + 双击抽牌，
	/// 手牌是横排 + 翻开），而手改过的 <c>project.json</c> 里往往只写了
	/// <c>id</c>/<c>name</c>/<c>rect</c>。反序列化之后其余字段停在
	/// <c>ZoneDefinition</c> 的 C# 默认值上 —— 对一个牌库来说那就是错的。
	///
	/// 但反过来"无脑全套出厂值"会把用户<b>改过</b>的字段冲掉
	/// （比如他把 <c>maxCards</c> 从 10 改成 5，而 5 恰好不是出厂默认）。
	/// 所以判据必须是"这个字段在文件里出现过没有"—— 只有 <c>JsonDocument</c>
	/// 知道这件事（反序列化之后"没写"和"写了默认值"长得一模一样）。
	/// </summary>
	private static void FillZoneDefaults(SaveProject project, string rawJson)
	{
		var present = new List<HashSet<string>>();

		try
		{
			using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
			if (doc.RootElement.TryGetProperty("zones", out System.Text.Json.JsonElement zonesEl) &&
				zonesEl.ValueKind == System.Text.Json.JsonValueKind.Array)
			{
				foreach (System.Text.Json.JsonElement z in zonesEl.EnumerateArray())
				{
					var names = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
					if (z.ValueKind == System.Text.Json.JsonValueKind.Object)
					{
						foreach (System.Text.Json.JsonProperty p in z.EnumerateObject())
							names.Add(p.Name);
					}

					present.Add(names);
				}
			}
		}
		catch (System.Text.Json.JsonException)
		{
			// 解析不了就不补 —— 调用方马上会因为 project 为 null 而报错，
			// 这里再抛一次只会把真正的错因盖掉。
			return;
		}

		for (int i = 0; i < project.Zones.Count; i++)
		{
			ZoneDefinition raw = project.Zones[i];
			HashSet<string> written = i < present.Count
				? present[i]
				: new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

			project.Zones[i] = MergeZoneDefaults(raw, written);
		}
	}

	/// <summary>把"文件里没写的字段"用该类型区域的出厂值填上。</summary>
	private static ZoneDefinition MergeZoneDefaults(ZoneDefinition raw, HashSet<string> written)
	{
		ZoneDefinition factory = ZoneDefaults.Create(raw.Kind, raw.Id, raw.Name, raw.Rect.Position);

		// 每加一个区域字段就要在这里补一行。漏了不会报错 ——
		// 症状是"手改 project.json 的这个字段没生效"，所以 json 那一节
		// 常驻一份字段清单（json_field_names）供核对。
		if (!written.Contains("rect")) factory.Rect = raw.Rect;
		if (written.Contains("tint")) factory.Tint = raw.Tint;
		if (written.Contains("borderColor")) factory.BorderColor = raw.BorderColor;
		if (written.Contains("sortMode")) factory.SortMode = raw.SortMode;
		if (written.Contains("faceOnEnter")) factory.FaceOnEnter = raw.FaceOnEnter;
		if (written.Contains("snapOnDrop")) factory.SnapOnDrop = raw.SnapOnDrop;
		if (written.Contains("maxCards")) factory.MaxCards = raw.MaxCards;
		if (written.Contains("drawOnDoubleClick")) factory.DrawOnDoubleClick = raw.DrawOnDoubleClick;
		if (written.Contains("drawTargetId")) factory.DrawTargetId = raw.DrawTargetId;
		if (written.Contains("drawCount")) factory.DrawCount = raw.DrawCount;
		if (written.Contains("enabled")) factory.Enabled = raw.Enabled;

		return factory;
	}

	// ------------------------------------------------------------------ state.json

	public static bool WriteState(string saveName, ObjectManager objects, ZoneManager zones)
	{
		AppPaths.EnsureSaveLayout(saveName);

		var state = new SaveState
		{
			FormatVersion = SaveState.CurrentFormatVersion,
			NextUidSeq = objects.UidSequence,
			Objects = objects.CaptureAllStates(),
		};

		// 区域成员次序必须单独存一份：物件身上的 ZoneId 只是"我属于哪个区域"的声明，
		// "谁是顶牌"完全由 Zone.Members 的次序决定，而在物件身上查不到。
		// 少了这一份，读档之后牌库的成员表是空的、牌却自称在牌库里 ——
		// 那正是自检读到的 member_counts_match 判红。
		foreach (Zone zone in zones.AllZones)
		{
			var members = new List<string>(zone.Count);
			foreach (TabletopObject m in zone.Members)
			{
				if (GodotObject.IsInstanceValid(m))
					members.Add(m.Uid);
			}

			state.ZoneMembers[zone.Id] = members;
		}

		return WriteText(AppPaths.StateFile(saveName), SaveJson.Serialize(state));
	}

	public static SaveState? ReadState(string saveName)
	{
		string path = AppPaths.StateFile(saveName);
		if (!FileAccess.FileExists(path))
		{
			LastError = $"没有 {path}";
			return null;
		}

		SaveState? state = SaveJson.Deserialize<SaveState>(ReadText(path) ?? "");
		if (state is null)
			LastError = $"{path} 解析失败（已打警告）";

		return state;
	}

	// ------------------------------------------------------------------ 存 / 读 一整桌

	/// <summary>把当前这一桌写进存档目录。</summary>
	public static bool Save(
		string saveName, ObjectManager objects, ZoneManager zones, BoardTheme theme)
	{
		AppPaths.EnsureSaveLayout(saveName);

		SaveProject project = CaptureProject(objects, theme);
		CaptureZones(project, zones);

		if (!WriteProject(saveName, project))
			return false;

		if (!WriteState(saveName, objects, zones))
			return false;

		AppPaths.WriteLastSave(saveName);
		SaveCount++;
		LastError = "";
		return true;
	}

	/// <summary>上一次读档失败时各区域的"成员账本 vs 声明数"现场（自检写进报告用）。</summary>
	public static string LastZoneLedger { get; private set; } = "";

	/// <summary>
	/// 读档：<b>整个场景按存档重建</b>。
	///
	/// 顺序不能反，而且是三步而不是两步：
	/// <list type="number">
	/// <item>清掉当前一切（物件 + 区域 + 定义池 + 历史）——
	///   旧历史对新场景没有意义，留着会让 <c>Ctrl+Z</c> 把新读的档改回去。</item>
	/// <item>装定义：卡牌 / Token / 区域定义 / 桌面主题。物件要靠定义才能造出来。</item>
	/// <item>造物件，<b>再</b>让区域认领（<c>ZoneId</c> 只是声明，配不上的话
	///   区域成员表就会指向尚不存在的物件）。</item>
	/// </list>
	///
	/// 最后跑一遍八条区域不变量：<b>读档也要被裁判检查</b>，
	/// 否则"读进来少一张牌"这种错只能靠肉眼发现。
	/// </summary>
	public static bool Load(
		string saveName, ObjectManager objects, ZoneManager zones, BoardTheme theme,
		UndoSystem undo, out string failure)
	{
		failure = "";

		SaveProject? project = ReadProject(saveName);
		if (project is null)
		{
			failure = LastError;
			return false;
		}

		SaveState? state = ReadState(saveName);
		if (state is null)
		{
			failure = LastError;
			return false;
		}

		// 1. 清场
		undo.Reset();
		zones.ClearAll();
		objects.ClearAll();

		// 2. 装定义
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

		objects.CardDefinitions.Clear();
		foreach (CardDefinition card in project.Cards)
			objects.CardDefinitions[card.Id] = card;

		objects.TokenDefinitions.Clear();
		foreach (TokenDefinition token in project.Tokens)
			objects.TokenDefinitions[token.Id] = token;

		foreach (ZoneDefinition def in project.Zones)
			zones.AddZone(def);

		// 3. 物件与区域成员：<b>交给 <c>SceneSnapshot.Restore</c>，不自己再写一遍</b>。
		//
		//    它内部已经做完了这一整串，而且顺序是"查了很久才定下来"的那一版：
		//    区域对齐 → 丢弃陈旧自由堆 → 物件状态 → 重建自由堆 → 区域成员 +
		//    ApplyLayout → 绘制次序 → 归一化派生残留 → uid 复位。
		//    "解散旧堆排在半途会把刚写好的 PileIndex 清掉"就是在那里查出来的 ——
		//    读档若自己重写一遍，等于把那个坑重新挖开，而且两份实现迟早分叉。
		//
		//    差别只有一个：数据来自磁盘而不是内存。
		//
		//    <b>但区域定义必须一并放进快照。</b>
		//
		//    这不是可选的：<c>RestoreZones</c> 会把"场景里有、快照里没有"的区域
		//    <b>删掉</b>。若只放物件不放区域，刚建好的区域会被它自己删掉，
		//    而成员的 <c>ZoneId</c> 还指着它们 —— 正是"孤儿声明"那条不变量报的东西。
		//    自检第一次跑就读到了：load_error = 读档之后一致性被破坏：no_orphan_zone_claims，
		//    而逐字段比对全是 0 差异（数据其实是对的，坏的是那层关系）。
		var snap = new SceneSnapshot { NextUidSeq = state.NextUidSeq };

		foreach (ZoneDefinition def in project.Zones)
			snap.Zones.Add(def.Clone());

		// 成员次序来自存档；旧存档（或手改掉了这一段）没有就退回"按 ZoneId 声明重建"，
		// 并把次序按物件在 state.Objects 里的顺序推出来 —— 总比拼不出来强。
		if (state.ZoneMembers.Count == 0)
		{
			GD.PushWarning("[SaveSystem] state.json 里没有 zoneMembers，退回按物件声明重建（顶牌次序可能与存档时不同）");

			foreach (ZoneDefinition def in project.Zones)
				snap.ZoneMembers[def.Id] = new List<string>();

			foreach (ObjectState s in state.Objects)
			{
				if (s.ZoneId.Length > 0 && snap.ZoneMembers.TryGetValue(s.ZoneId, out List<string>? list))
					list.Add(s.Uid);
			}
		}
		else
		{
			foreach (KeyValuePair<string, List<string>> kv in state.ZoneMembers)
				snap.ZoneMembers[kv.Key] = new List<string>(kv.Value);
		}

		snap.Objects.AddRange(state.Objects);
		snap.Restore(objects, zones);

		int skipped = state.Objects.Count - objects.ObjectCount;

		// 4. 裁判复查
		ZoneInvariantReport check = ZoneInvariants.Check(objects, zones);
		if (!check.All)
		{
			failure = $"读档之后一致性被破坏：{string.Join(", ", check.Failures())}";
			GD.PushError($"[SaveSystem] {failure}");

			// 把现场写下来。"成员账本对不上"这类失败光看名字完全不知道该看哪 —
			// 而它在读档路径上会连带弄红一大片（含"坏定义优雅降级"那条与它无关的断言）。
			var detail = new System.Text.StringBuilder();
			foreach (Zone zone in zones.AllZones)
			{
				int claiming = 0;
				foreach (TabletopObject obj in objects.AllObjects)
				{
					if (obj.ZoneId == zone.Id)
						claiming++;
				}

				detail.Append($"[{zone.Id} 成员={zone.Count} 声明={claiming}] ");
			}

			GD.PushError($"[SaveSystem] 区域账本：{detail}");
			LastZoneLedger = $"{detail}  ||  写回现场：{SceneSnapshot.LastRestoreNote}";
			return false;
		}

		objects.NotifyRestored();
		AppPaths.WriteLastSave(saveName);
		LoadCount++;

		if (skipped > 0)
			GD.PushWarning($"[SaveSystem] {saveName} 里有 {skipped} 个物件的定义不存在，已跳过");

		return true;
	}

	// ------------------------------------------------------------------ 存档列表与增删

	/// <summary>存档列表里的一行。</summary>
	public readonly record struct Entry(string Name, string Modified, int ObjectCount, bool HasThumb);

	/// <summary>列出全部存档（按目录名排序，附最后修改时间与物件数）。</summary>
	public static List<Entry> ListSaves()
	{
		var list = new List<Entry>();

		foreach (string name in AppPaths.ListSaveNames())
		{
			string stateFile = AppPaths.StateFile(name);
			string modified = "—";
			int objectCount = 0;

			if (FileAccess.FileExists(stateFile))
			{
				// 文件修改时间：给人看的排序参考。
				// 全限定 System.DateTime：Godot 命名空间里也有一个同名的 DateTime，
				// `using Godot;` 会把它带进来遮蔽 BCL 那个（与 JsonStringEnumConverter 同一个坑）。
				ulong ticks = FileAccess.GetModifiedTime(stateFile);
				if (ticks > 0)
				{
					modified = System.DateTimeOffset
						.FromUnixTimeSeconds((long)ticks)
						.ToLocalTime()
						.ToString("M/d HH:mm");
				}

				SaveState? st = SaveJson.Deserialize<SaveState>(ReadText(stateFile) ?? "");
				objectCount = st?.Objects.Count ?? 0;
			}

			string thumbPath = $"{AppPaths.ThumbsDir(name)}/board.png";
			list.Add(new Entry(name, modified, objectCount, FileAccess.FileExists(thumbPath)));
		}

		return list;
	}

	/// <summary>删除一个存档目录。<b>不可撤销</b> —— 界面必须二次确认。</summary>
	public static bool DeleteSave(string saveName)
	{
		string dir = AppPaths.SaveDir(saveName);
		if (!DirAccess.DirExistsAbsolute(dir))
			return false;

		if (RemoveRecursive(dir) is { } err && err != Error.Ok)
		{
			LastError = $"删不掉 {dir}：{err}";
			return false;
		}

		// 删的正好是"上次打开的" → 把这一行清掉，免得下次启动指着一个不存在的存档。
		// 判据写成"这个存档名是不是当前指向的那个"，而不是"删的这次是不是它"——
		// 前者与调用顺序无关，后者要依赖"删之前刚好没改过名"。
		if (AppPaths.ReadLastSave() == saveName)
			AppPaths.ClearLastSave();

		return true;
	}

	/// <summary>
	/// 递归删目录。
	///
	/// 自己写而不是找现成的：Godot 4.7 的 <c>DirAccess</c> 只有
	/// <c>Remove</c>（删文件）与 <c>RemoveAbsolute</c>，<b>没有删目录树的方法</b>。
	/// 一个存档目录里有 <c>images/</c>、<c>thumbs/</c> 两个子目录，
	/// 所以必须自己递归 —— 只删顶层文件会留下一个半死的目录，
	/// 而它在下拉列表里仍然列得出来。
	/// </summary>
	private static Error RemoveRecursive(string dir)
	{
		using DirAccess? d = DirAccess.Open(dir);
		if (d is null)
			return DirAccess.GetOpenError();

		foreach (string file in d.GetFiles())
		{
			Error e = d.Remove(file);
			if (e != Error.Ok)
				return e;
		}

		foreach (string sub in d.GetDirectories())
		{
			Error e = RemoveRecursive($"{dir}/{sub}");
			if (e != Error.Ok)
				return e;
		}

		// DirAccess 持有目录句柄时删不掉它自己，所以关掉再删
		d.Dispose();
		return DirAccess.RemoveAbsolute(dir);
	}

	/// <summary>重命名（目录改名 + 同步 last_save）。</summary>
	public static bool RenameSave(string oldName, string newName)
	{
		string from = AppPaths.SaveDir(oldName);
		string to = AppPaths.SaveDir(newName);

		if (oldName == newName)
			return true;

		if (DirAccess.DirExistsAbsolute(to))
		{
			LastError = $"已经有一个叫「{newName}」的存档了";
			return false;
		}

		Error err = DirAccess.RenameAbsolute(from, to);
		if (err != Error.Ok)
		{
			LastError = $"改名失败：{err}";
			return false;
		}

		if (AppPaths.ReadLastSave() == oldName)
			AppPaths.WriteLastSave(newName);

		return true;
	}

	/// <summary>一个存档名是否可用（不空、不与现有目录重名）。</summary>
	public static bool IsNameAvailable(string saveName)
	{
		if (string.IsNullOrWhiteSpace(saveName))
			return false;

		return !DirAccess.DirExistsAbsolute(AppPaths.SaveDir(saveName));
	}

	// ------------------------------------------------------------------ 文件小工具

	private static bool WriteText(string path, string text)
	{
		using FileAccess? f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
		if (f is null)
		{
			LastError = $"写不了 {path}：{FileAccess.GetOpenError()}";
			GD.PushError($"[SaveSystem] {LastError}");
			return false;
		}

		f.StoreString(text);
		return true;
	}

	private static string? ReadText(string path)
	{
		using FileAccess? f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		if (f is null)
		{
			LastError = $"读不了 {path}：{FileAccess.GetOpenError()}";
			return null;
		}

		return f.GetAsText();
	}
}

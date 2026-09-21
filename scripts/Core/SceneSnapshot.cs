using System.Collections.Generic;
using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 一桌的完整状态。<b>撤销、读档、日志跳转都用它。</b>
///
/// M2 写 <c>ObjectState</c> 时就定下了这条路：
/// 「撤销不是「反向操作」，而是「把快照写回去」；存档就是把一堆快照存成 JSON」。
/// 这个类就是那句话的落地。
///
/// 为什么不做"每个命令写自己的逆操作"：并堆的逆是拆堆 + 恢复次序 + 恢复锚点；
/// 抽牌的逆是放回牌库<b>顶</b>并恢复盖放；区域移出的逆要同时恢复
/// <c>PileIndex</c>/<c>PileCount</c>/<c>Visible</c> 三个字段。
/// <b>漏一个字段就是"数据全对、只是画错"那类 bug</b> —— M3 的张数徽章残留
/// 正是这么来的。一条恢复路径覆盖全部命令，正确性只取决于"快照是否完整"，
/// 而完整性是可断言的。
/// </summary>
internal sealed class SceneSnapshot
{
	/// <summary>全部物件，<b>顺序即绘制次序</b>（谁压谁）。</summary>
	internal List<ObjectState> Objects { get; } = new();

	/// <summary>区域 id → 成员 uid 次序（底 → 顶）。空表示该区域没有成员。</summary>
	internal Dictionary<string, List<string>> ZoneMembers { get; } = new();

	/// <summary>
	/// 区域定义 + 建区次序。<b>不能只存成员</b> —— 区域本身也是可被增删的状态。
	///
	/// 这条是自检逼出来的：<c>zone_teardown</c> 会把所有区域删掉，
	/// 而"撤销"要能把桌子恢复成开局那样 —— 没有这一份，
	/// 写回之后牌还指着 <c>demo.zone.deck</c>、区域却已经不存在了，
	/// 正是"孤儿声明"那条不变量报的东西。
	///
	/// 存 <see cref="ZoneDefinition.Clone"/> 而不是原对象：定义是共享引用，
	/// 将来 M5 的编辑器会就地改它，那么旧快照里的定义会跟着一起变 ——
	/// 撤销就会"撤"到一个被改过的定义上。
	/// </summary>
	internal List<ZoneDefinition> Zones { get; } = new();

	/// <summary><c>ObjectManager.UidSequence</c>。不存它，撤销之后新建的物件会撞 uid。</summary>
	internal int NextUidSeq { get; set; }

	/// <summary>本次写回删掉了多少个"快照里没有"的物件。写进自检报告供人核对。</summary>
	internal int LastExtraRemoved { get; private set; }

	internal int Count => Objects.Count;

	/// <summary>
	/// 两份快照是否<b>内容相同</b>。
	///
	/// 撤销系统靠它回答"这次操作到底改了什么"：没改就不进历史。
	/// 少了这一条，用户点一下没拖动也会占一条历史，他按 <c>Ctrl+Z</c>
	/// 时会觉得"按了没反应"（其实是撤销了一次空操作）。
	///
	/// 比的是"物件集合 + 每个物件的内容 + 绘制次序 + 区域成员次序 + uid 计数器"。
	/// <b>不比物件在列表里的位置之外的东西</b> —— 快照里也没有别的了。
	/// </summary>
	internal static bool SameContent(SceneSnapshot a, SceneSnapshot b)
	{
		if (ReferenceEquals(a, b))
			return true;

		if (a.Objects.Count != b.Objects.Count)
			return false;

		if (a.NextUidSeq != b.NextUidSeq)
			return false;

		if (a.Zones.Count != b.Zones.Count)
			return false;

		// 物件按绘制次序逐个比 —— 次序本身是状态（谁压谁）
		for (int i = 0; i < a.Objects.Count; i++)
		{
			if (!a.Objects[i].SameAs(b.Objects[i]))
				return false;
		}

		if (a.ZoneMembers.Count != b.ZoneMembers.Count)
			return false;

		foreach (KeyValuePair<string, List<string>> kv in a.ZoneMembers)
		{
			if (!b.ZoneMembers.TryGetValue(kv.Key, out List<string>? other))
				return false;

			if (kv.Value.Count != other.Count)
				return false;

			for (int i = 0; i < kv.Value.Count; i++)
			{
				if (kv.Value[i] != other[i])
					return false;
			}
		}

		// 区域定义也要比：M5 的编辑器会改定义，那时"改了一个区域参数"必须算一次操作
		for (int i = 0; i < a.Zones.Count; i++)
		{
			if (!SameDefinition(a.Zones[i], b.Zones[i]))
				return false;
		}

		return true;
	}

	/// <summary>两份区域定义是否逐字段相同（只比进 JSON 的那些字段）。</summary>
	private static bool SameDefinition(ZoneDefinition a, ZoneDefinition b)
		=> SaveJson.Serialize(a) == SaveJson.Serialize(b);

	/// <summary>
	/// 抓一份当前快照。
	///
	/// <b>物件顺序必须是 <c>_drawOrder</c> 的顺序</b>：画面上谁压谁完全由它决定。
	/// M3 的整摞翻转踩过这个坑 —— 次序变了但绘制次序没同步，
	/// 看起来像"翻转没生效"。撤销要恢复的是<b>画面</b>，所以次序是状态的一部分。
	/// </summary>
	internal static SceneSnapshot Capture(ObjectManager objects, ZoneManager zones)
	{
		var snap = new SceneSnapshot
		{
			NextUidSeq = objects.UidSequence,
		};

		snap.Objects.AddRange(objects.CaptureAllStates());

		foreach (Zone zone in zones.AllZones)
		{
			snap.Zones.Add(zone.Definition.Clone());

			var members = new List<string>(zone.Count);
			foreach (TabletopObject m in zone.Members)
			{
				if (GodotObject.IsInstanceValid(m))
					members.Add(m.Uid);
			}

			snap.ZoneMembers[zone.Id] = members;
		}

		return snap;
	}

	/// <summary>
	/// 把这份快照写回场景。
	///
	/// <b>不返回任何东西</b> —— 写回去之后是否真的一致，由
	/// <see cref="ZoneInvariants.Check"/> 复查，而不是在这里猜。
	/// 调用方（<c>UndoSystem</c>）负责复查并在不一致时打红日志。
	/// </summary>
	internal void Restore(ObjectManager objects, ZoneManager zones)
	{
		// 0. 区域本身先对齐：快照里有的必须存在，快照里没有的必须消失。
		//
		//    这一步<b>必须排在物件之前</b>，因为"物件属于哪个区域"要按 id 对上；
		//    区域不存在时，写回 ZoneId 只会造出一批孤儿声明。
		RestoreZones(zones);

		// 旧的自由堆结构先丢掉。<b>必须在这里</b>，不能等到 RebuildPiles ——
		// 旧堆是上一轮的陈旧结构（它的成员关系与快照并不一致），
		// 拖到后面解散会把快照写回过程中刚写好的 PileIndex 一起清掉。
		// 详见 ObjectManager.DropAllPiles 的说明。
		objects.DropAllPiles();

		// 1. 找出"快照里没有"的物件并删掉。
		//
		//    用 HashSet 而不是"逐个去 Objects 里线性找"：物件多起来之后
		//    那是 O(n×m)，而撤销是高频操作，每次 O(n²) 会直接卡在拖拽回退上。
		var snapUids = new HashSet<string>();
		foreach (ObjectState s in Objects)
			snapUids.Add(s.Uid);

		var extra = new List<TabletopObject>();
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (!snapUids.Contains(obj.Uid))
				extra.Add(obj);
		}

		if (extra.Count > 0)
		{
			zones.ForgetObjects(extra);      // 先让区域/堆松手，别留野引用

			// <b>必须走"不记历史"的那条删除。</b>
			//
			// 这里原本调的是面向用户动作的 DeleteObjects，于是写回过程中会插进一条
			// "删除 N 个物件"：它落在被撤销的那一条<b>之前</b>，
			// 正好触发"在时间线中间做新操作 → 丢掉后面的重做"，
			// 而 _current 也被改写成写了一半的快照。
			// 用户看到的就是「撤销把桌面清空、Ctrl+Y 再也回不来」。
			objects.DeleteObjectsWithoutHistory(extra);
		}

		LastExtraRemoved = extra.Count;

		// 2. 逐张写回状态；快照里有、场景里没有的补造出来。
		//
		//    索引在<b>删除之后</b>重建 —— 删除之前抓的那份会留着已释放对象的引用，
		//    而 Godot 的节点被 QueueFree 之后，引用还在、访问才炸。
		var byUid = new Dictionary<string, TabletopObject>();
		foreach (TabletopObject obj in objects.AllObjects)
			byUid[obj.Uid] = obj;

		var ordered = new List<TabletopObject>(Objects.Count);
		foreach (ObjectState s in Objects)
		{
			if (byUid.TryGetValue(s.Uid, out TabletopObject? existing) &&
				GodotObject.IsInstanceValid(existing))
			{
				existing.ApplyState(s);
				ordered.Add(existing);
				continue;
			}

			TabletopObject? created = objects.InstantiateFromState(s);
			if (created is null)
			{
				// 定义缺失（存档被手改坏了）—— InstantiateFromState 已经打过警告，
				// 这里不重复报。该物件在快照里有、场景里没有，是<b>预期内的降级</b>。
				continue;
			}

			created.ApplyState(s);
			byUid[s.Uid] = created;
			ordered.Add(created);
		}

		// 3. 自由堆重建（按 PileId 分组、PileIndex 定序）。
		//    排在区域<b>之前</b>：自由堆的成员恒有 ZoneId == ""，
		//    而区域排版（ApplyStack 等）会按成员次序重写 PileIndex / Visible ——
		//    两条权威各管各的对象集合，顺序上让区域最后说话，
		//    才能保证"叠放区域的成员序号恰好连续"这类不变量成立。
		objects.RebuildPiles(Objects);

		// 4. 区域成员重建。先全部清空再按快照填 ——
		//    不能只做"补差"，因为快照里的次序本身就是数据（牌库的顶牌是谁）。
		//
		//    用 AddMember 而不是 ZoneManager.MoveInto：后者会做容量校验、
		//    朝向策略与落点吸附，那都是"用户动作"的语义。
		//
		//    清空这一步同时把<b>已经失效的成员</b>一并清掉：上一步刚删掉的物件
		//    可能还挂在 zone.Members 里（Godot 的释放是帧末的）。留着它会让
		//    "无孤儿声明"那条不变量当场变红 —— 而那正是撤销最该避免的残留。
		foreach (Zone zone in zones.AllZones)
		{
			zone.Members.Clear();
			if (ZoneMembers.TryGetValue(zone.Id, out List<string>? uids))
			{
				foreach (string uid in uids)
				{
					if (byUid.TryGetValue(uid, out TabletopObject? m) &&
						GodotObject.IsInstanceValid(m))
					{
						zone.AddMember(m);
					}
				}
			}

			// <b>必须调 ApplyLayout</b>，而且必须排在物件状态写回<b>之后</b>：
			// 排版类区域（Stack / Row / Fan）的位置与可见性<b>由区域自己算</b>，
			// 不是从物件身上读的 —— 比如"牌库只画最上面 3 张"就是
			// ApplyStack 里 m.Visible = i >= count - PileVisibleDepth。
			//
			// 那为什么写回后位置仍然逐字段严格相等？因为 ApplyLayout 是
			// <b>确定性</b>的：StackAnchor 由 ZoneDefinition.Rect 算出来、
			// 成员次序由快照决定，同样的输入必然算出同样的输出。
			// 真出现差异就说明"快照里的位置"和"排版算出来的位置"本来就不一致 ——
			// 那是真 bug，正好该被断言抓住。
			zone.ApplyLayout();
		}

		// 5. 绘制次序：整体重置成快照里的顺序（谁压谁是状态的一部分）
		objects.RestoreDrawOrder(ordered);

		// 6. 叠放残留归一化（见方法说明）
		NormalizeStackLeftovers(objects, zones);

		// 7. uid 计数器复位（否则撤销之后新建的物件会与还原出来的物件撞 uid）
		objects.UidSequence = NextUidSeq;

		objects.ClearSelection();
	}

	/// <summary>
	/// 把"叠放残留"字段归一：<c>PileCount</c> 要么等于它所属叠放组的真实张数，要么为 0。
	///
	/// 为什么恢复快照时必须做这一步：<c>PileCount</c> 是<b>派生值</b> ——
	/// 它等于所在堆 / 叠放区域的成员数，所以没有进 <c>ObjectState</c>（也就没进快照）。
	/// 于是恢复之后，它保留的是<b>写回之前那一刻</b>的值，而那个值完全可能已经过期。
	///
	/// 实测撞到的正是这个：两张散牌身上留着"上一轮某个堆解散后剩下的 PileCount=2"，
	/// 恢复快照之后它们既不在堆里、也不在任何区域里，却挂着 2 ——
	/// 与 M3 用户实测抓到的「从牌库拖出来还挂着张数」是同一类残留。
	/// 快照写回只负责它<b>存了</b>的字段，所以这类派生残留必须由恢复这一步补上。
	/// </summary>
	private static void NormalizeStackLeftovers(ObjectManager objects, ZoneManager zones)
	{
		foreach (TabletopObject obj in objects.AllObjects)
		{
			if (!GodotObject.IsInstanceValid(obj))
				continue;

			if (obj.PileId != 0 && objects.Piles.TryGetValue(obj.PileId, out Pile? pile))
			{
				obj.PileCount = pile.Count;
				continue;
			}

			if (!string.IsNullOrEmpty(obj.ZoneId) && zones.Find(obj.ZoneId) is Zone owner
				&& owner.Definition.SortMode == ZoneSortMode.Stack)
			{
				obj.PileCount = owner.Count;
				continue;
			}

			// 不在任何叠放组里 —— 张数不许留着（ClearStackVisual 会一并复位可见性）
			if (obj.PileCount != 0)
				obj.ClearStackVisual();
		}
	}

	/// <summary>
	/// 把区域集合对齐到快照：缺的造出来、多的删掉、<b>已有区域的定义逐字段写回</b>。
	///
	/// 顺序按快照的建区次序重建，因为 <c>ZoneManager</c> 的重叠命中规则是
	/// "取最后添加的"（视觉上画在上面的那个）—— 次序本身就是语义。
	///
	/// 用 <c>AddZone</c> 而不是自己 new 一个节点：区域的成员数广播与
	/// 次序同步回调都挂在那里，绕过它建出来的区域不会通知 HUD。
	///
	/// <b>第三件事（"已有区域的定义"）是修完一个真 bug 之后补的。</b>
	/// 原来这里只在"区域不存在"时用快照里的定义建它，已有区域的定义<b>一个字段都不动</b>：
	/// 于是"锁定此区域"这类<b>只改区域定义</b>的动作撤销不掉 ——
	/// 牌序、成员、位置全都正确恢复，唯独那块区域的锁还挂着。
	///
	/// 它极难自己暴露：<c>ZoneDefinition</c> 是引用类型，
	/// 而"用户动作"是就地改它、快照存的是 <c>Clone()</c> ——
	/// 于是 <c>ReferenceEquals(zone.Definition, snapshot.Definition)</c> 永远为假，
	/// 靠"引用相同就跳过"这类优化根本发现不了漏写。
	/// 抓它的是 <c>history_simulation.undo_restores_exactly</c>：
	/// 差异只报出一行"区域定义 demo.zone.deck"，剩下全绿。
	/// </summary>
	private void RestoreZones(ZoneManager zones)
	{
		var wanted = new HashSet<string>();
		foreach (ZoneDefinition def in Zones)
			wanted.Add(def.Id);

		var doomed = new List<Zone>();
		foreach (Zone zone in zones.AllZones)
		{
			if (!wanted.Contains(zone.Id))
				doomed.Add(zone);
		}

		foreach (Zone zone in doomed)
			zones.RemoveZone(zone);

		foreach (ZoneDefinition def in Zones)
		{
			if (zones.Find(def.Id) is Zone existing)
			{
				// 已有区域：定义逐字段写回（Enabled / SortMode / FaceOnEnter / MaxCards / Tint…）
				existing.ApplyDefinition(def.Clone());
				continue;
			}

			zones.AddZone(def.Clone());
		}
	}
}

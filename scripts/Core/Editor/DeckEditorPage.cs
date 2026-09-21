using Godot;
using TabletopSimulator.Core.Objects;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;

/// <summary>
/// 「卡组」页：一副牌由"哪种卡几张"组成，改完可以一键发到牌库或桌面。
///
/// <b>它与「卡牌」页的分工：</b>卡牌页改的是"这张卡长什么样"，
/// 卡组页改的是"这副牌里有哪些卡、各几张"。两者靠 id 引用连起来 ——
/// 于是改卡面数值不需要碰卡组，改张数也不需要碰卡面。
/// </summary>
public partial class DeckEditorPage : EditorPage
{
	private OptionButton _deckPicker = null!;
	private VBoxContainer _rows = null!;
	private Label _summary = null!;
	private OptionButton _cardPicker = null!;

	/// <summary>当前正在编辑的卡组 id。</summary>
	public string SelectedDeckId { get; private set; } = "";

	/// <summary>发牌目标的区域 id（空 = 直接放在桌面中心的散件）。</summary>
	public string TargetZoneId { get; private set; } = "";

	/// <summary>当前卡组（不存在则 null）。</summary>
	public CardDeck? SelectedDeck =>
		SelectedDeckId.Length > 0 && Objects.Decks.TryGetValue(SelectedDeckId, out CardDeck? deck)
			? deck
			: null;

	protected override void BuildContent()
	{
		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 8);
		column.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(column);

		var top = new HBoxContainer();
		top.AddThemeConstantOverride("separation", 8);
		column.AddChild(top);

		top.AddChild(new Label { Text = "卡组：" });
		_deckPicker = new OptionButton { CustomMinimumSize = new Vector2(220f, 0f) };
		_deckPicker.ItemSelected += OnDeckPicked;
		top.AddChild(_deckPicker);

		var newDeck = new Button { Text = "新建卡组" };
		newDeck.Pressed += OnNewDeckPressed;
		top.AddChild(newDeck);

		var deleteDeck = new Button { Text = "删除卡组" };
		deleteDeck.Pressed += OnDeleteDeckPressed;
		top.AddChild(deleteDeck);

		_summary = new Label { Text = "" };
		column.AddChild(_summary);

		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		column.AddChild(scroll);

		_rows = new VBoxContainer { Name = "DeckRows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_rows.AddThemeConstantOverride("separation", 4);
		scroll.AddChild(_rows);

		var addRow = new HBoxContainer();
		addRow.AddThemeConstantOverride("separation", 8);
		column.AddChild(addRow);

		addRow.AddChild(new Label { Text = "加一张：" });
		_cardPicker = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		addRow.AddChild(_cardPicker);

		var add = new Button { Text = "加入卡组" };
		add.Pressed += OnAddPressed;
		addRow.AddChild(add);

		var actions = new HBoxContainer();
		actions.AddThemeConstantOverride("separation", 8);
		column.AddChild(actions);

		actions.AddChild(new Label { Text = "发到：" });
		OptionButton target = BuildTargetPicker();
		actions.AddChild(target);

		var deal = new Button { Text = "按这副牌发一副" };
		deal.Pressed += OnDealPressed;
		actions.AddChild(deal);

		var toTable = new Button { Text = "散着放到桌面" };
		toTable.Pressed += OnScatterPressed;
		actions.AddChild(toTable);
	}

	private OptionButton BuildTargetPicker()
	{
		var picker = new OptionButton { Name = "TargetZonePicker", CustomMinimumSize = new Vector2(220f, 0f) };
		picker.AddItem("桌面中心（散件）", 0);
		picker.SetItemMetadata(0, "");
		picker.ItemSelected += idx =>
		{
			TargetZoneId = picker.GetItemMetadata((int)idx).AsString();
		};
		return picker;
	}

	public override void OnShown()
	{
		RefreshDeckPicker();
		RefreshRows();
		RefreshCardPicker();

		// 发牌目标要跟着区域变化重填 —— 区域是随时会加/删的（「区域」页），
		// 不重填的话"刚画好一个牌库，却发现发牌目标里没有它"。
		RefreshTargetPicker();
	}

	// ------------------------------------------------------------------ 列表刷新

	private void RefreshDeckPicker()
	{
		if (!IsInstanceValid(_deckPicker))
			return;

		_deckPicker.Clear();
		var ids = new System.Collections.Generic.List<string>(Objects.Decks.Keys);
		ids.Sort(System.StringComparer.OrdinalIgnoreCase);

		foreach (string id in ids)
		{
			CardDeck deck = Objects.Decks[id];
			string name = string.IsNullOrWhiteSpace(deck.DisplayName) ? id : deck.DisplayName;
			_deckPicker.AddItem($"{name}（{deck.TotalCards} 张）");
			_deckPicker.SetItemMetadata(_deckPicker.ItemCount - 1, id);
		}

		if (SelectedDeckId.Length == 0 && ids.Count > 0)
			SelectedDeckId = ids[0];

		for (int i = 0; i < _deckPicker.ItemCount; i++)
		{
			if (_deckPicker.GetItemMetadata(i).AsString() == SelectedDeckId)
			{
				_deckPicker.Selected = i;
				break;
			}
		}
	}

	private void RefreshRows()
	{
		if (!IsInstanceValid(_rows))
			return;

		foreach (Node child in _rows.GetChildren())
		{
			_rows.RemoveChild(child);
			child.QueueFree();
		}

		CardDeck? deck = SelectedDeck;
		if (deck is null)
		{
			_summary.Text = "还没有卡组。点「新建卡组」开始。";
			return;
		}

		_summary.Text = $"「{deck.DisplayName}」共 {deck.TotalCards} 张，{deck.Cards.Count} 种";

		for (int i = 0; i < deck.Cards.Count; i++)
		{
			CardStack stack = deck.Cards[i];
			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 6);

			string name = CardDefinitionService.DisplayNameOf(Objects, stack.CardId);
			row.AddChild(new Label
			{
				Text = $"{name}",
				CustomMinimumSize = new Vector2(200f, 0f),
				ClipText = true,
				TooltipText = stack.CardId,
			});

			var count = new SpinBox
			{
				MinValue = 0,
				MaxValue = 999,
				Step = 1,
				Value = stack.Count,
				CustomMinimumSize = new Vector2(90f, 0f),
			};

			int index = i;   // 闭包捕获：不取局部副本的话每行都会改到最后一行
			count.ValueChanged += v =>
			{
				CardDeck? target = SelectedDeck;
				if (target is null || index >= target.Cards.Count)
					return;

				target.Cards[index].Count = (int)v;
				SummaryRefreshOnly();
			};

			row.AddChild(count);

			var up = new Button { Text = "↑" };
			up.Pressed += () => MoveRow(index, -1);
			row.AddChild(up);

			var down = new Button { Text = "↓" };
			down.Pressed += () => MoveRow(index, 1);
			row.AddChild(down);

			var remove = new Button { Text = "移除" };
			remove.Pressed += () => RemoveRow(index);
			row.AddChild(remove);

			_rows.AddChild(row);
		}
	}

	private void RefreshCardPicker()
	{
		if (!IsInstanceValid(_cardPicker))
			return;

		_cardPicker.Clear();
		foreach (CardDefinition def in CardDefinitionService.ListCards(Objects))
		{
			string name = string.IsNullOrWhiteSpace(def.DisplayName) ? "（无名）" : def.DisplayName;
			_cardPicker.AddItem($"{name}　{def.Id}");
			_cardPicker.SetItemMetadata(_cardPicker.ItemCount - 1, def.Id);
		}
	}

	/// <summary>只重算那一行摘要文字（改张数时用，避免重建整张表单把焦点抢掉）。</summary>
	private void SummaryRefreshOnly()
	{
		CardDeck? deck = SelectedDeck;
		if (deck is not null && IsInstanceValid(_summary))
			_summary.Text = $"「{deck.DisplayName}」共 {deck.TotalCards} 张，{deck.Cards.Count} 种";

		Panel.NotifyChanged();
	}

	// ------------------------------------------------------------------ 动作

	private void OnDeckPicked(long index)
	{
		SelectedDeckId = _deckPicker.GetItemMetadata((int)index).AsString();
		RefreshRows();
	}

	private void OnNewDeckPressed()
	{
		CardDeck deck = CardDeckService.Create(Objects, $"卡组{Objects.Decks.Count + 1}");
		SelectedDeckId = deck.Id;
		RefreshDeckPicker();
		RefreshRows();
		Panel.NotifyChanged();
		Hud.Toast($"已新建卡组「{deck.DisplayName}」");
	}

	private void OnDeleteDeckPressed()
	{
		CardDeck? deck = SelectedDeck;
		if (deck is null)
			return;

		Objects.Decks.Remove(deck.Id);
		SelectedDeckId = "";
		RefreshDeckPicker();
		RefreshRows();
		Panel.NotifyChanged();
		Hud.Toast($"已删除卡组「{deck.DisplayName}」（牌库里的牌不受影响）");
	}

	private void OnAddPressed()
	{
		CardDeck? deck = SelectedDeck;
		if (deck is null || _cardPicker.ItemCount == 0)
			return;

		string cardId = _cardPicker.GetItemMetadata(_cardPicker.Selected).AsString();
		CardDeckService.AddCard(deck, cardId, 1);
		RefreshRows();
		Panel.NotifyChanged();
	}

	private void MoveRow(int index, int delta)
	{
		CardDeck? deck = SelectedDeck;
		if (deck is null)
			return;

		int target = index + delta;
		if (index < 0 || index >= deck.Cards.Count || target < 0 || target >= deck.Cards.Count)
			return;

		(deck.Cards[index], deck.Cards[target]) = (deck.Cards[target], deck.Cards[index]);
		RefreshRows();
		Panel.NotifyChanged();
	}

	private void RemoveRow(int index)
	{
		CardDeck? deck = SelectedDeck;
		if (deck is null || index < 0 || index >= deck.Cards.Count)
			return;

		deck.Cards.RemoveAt(index);
		RefreshRows();
		Panel.NotifyChanged();
	}

	private void OnDealPressed()
	{
		CardDeck? deck = SelectedDeck;
		if (deck is null)
			return;

		DealResult result = CardDeckService.DealIntoZone(Objects, Zones, Board, deck, TargetZoneId);
		Hud.Toast(result.Ok
			? $"「{deck.DisplayName}」已发 {result.Spawned} 张到{result.TargetLabel}"
			: $"发牌失败：{result.Error}");

		Panel.NotifyChanged();
	}

	private void OnScatterPressed()
	{
		CardDeck? deck = SelectedDeck;
		if (deck is null)
			return;

		int n = CardDeckService.ScatterOnTable(Objects, Board, deck);
		Hud.Toast($"「{deck.DisplayName}」已散 {n} 张到桌面");
		Panel.NotifyChanged();
	}

	/// <summary>发牌目标的区域下拉要跟着区域变化重填（区域页随时会加/删区域）。</summary>
	internal void RefreshTargetPicker()
	{
		if (!IsInstanceValid(this))
			return;

		if (FindChild("TargetZonePicker", recursive: true, owned: false) is not OptionButton picker)
			return;

		string keep = TargetZoneId;
		picker.Clear();
		picker.AddItem("桌面中心（散件）", 0);
		picker.SetItemMetadata(0, "");

		foreach (Zone zone in Zones.AllZones)
		{
			picker.AddItem($"{zone.Definition.Name}（{zone.Count} 张）");
			picker.SetItemMetadata(picker.ItemCount - 1, zone.Id);
		}

		// 把选择恢复到原来的目标（区域被删了就落回桌面）
		for (int i = 0; i < picker.ItemCount; i++)
		{
			if (picker.GetItemMetadata(i).AsString() == keep)
			{
				picker.Selected = i;
				return;
			}
		}

		TargetZoneId = "";
		picker.Selected = 0;
	}
}

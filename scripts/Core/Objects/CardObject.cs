using System.Collections.Generic;
using Godot;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core.Objects;

/// <summary>
/// 卡牌。外观由 <see cref="CardDefinition"/>（模板）+ <see cref="FieldOverrides"/>（实例覆盖）合成。
///
/// 实例覆盖是这个工具最关键的机制之一：验平衡性时你要反复改「这张牌的费用」，
/// 但不该污染定义、更不该影响牌库里的另外三张同名卡。改这里就只改这一张。
/// </summary>
public partial class CardObject : TabletopObject
{
	public override ObjectKind Kind => ObjectKind.Card;

	/// <summary>卡牌定义（模板）。</summary>
	public CardDefinition Definition { get; private set; } = new();

	/// <summary>实例级字段覆盖：键 → 值。空的键走定义里的值。</summary>
	public Dictionary<string, string> FieldOverrides { get; } = new();

	/// <summary>描边跟随卡面圆角，不然选中框是方角、卡是圆角，很怪。</summary>
	protected override float OutlineCornerRadius => Definition.Template.CornerRadius;

	public void SetDefinition(CardDefinition definition)
	{
		Definition = definition;
		QueueRedraw();
	}

	/// <summary>设置一个实例级字段覆盖。</summary>
	public void SetFieldOverride(string key, string value)
	{
		FieldOverrides[key] = value;
		QueueRedraw();
	}

	/// <summary>清掉某个字段的覆盖，回到定义值。</summary>
	public void ClearFieldOverride(string key)
	{
		if (FieldOverrides.Remove(key))
			QueueRedraw();
	}

	/// <summary>这张卡当前生效的字段值（覆盖优先）。</summary>
	public string GetFieldValue(string key) => Definition.GetFieldValue(key, FieldOverrides);

	/// <summary>这张卡当前生效的整数值（改平衡性时直接读这个）。</summary>
	public int GetFieldInt(string key) => Definition.GetFieldInt(key, FieldOverrides);

	protected override void DrawContent()
	{
		if (IsFaceDown)
			CardFaceRenderer.DrawBack(this, Definition, LocalRect);
		else
			CardFaceRenderer.DrawFace(this, Definition, LocalRect, FieldOverrides);
	}

	protected override void CaptureExtra(ObjectState state)
	{
		state.DefinitionId = Definition.Id;
		state.FieldOverrides = new Dictionary<string, string>(FieldOverrides);
	}

	protected override void ApplyExtra(ObjectState state)
	{
		// Definition 本体由 ObjectManager 按 DefinitionId 注入（这里拿不到卡池）
		FieldOverrides.Clear();
		foreach (KeyValuePair<string, string> kv in state.FieldOverrides)
			FieldOverrides[kv.Key] = kv.Value;
	}
}

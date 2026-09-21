using System.Collections.Generic;
using TabletopSimulator.Data;

namespace TabletopSimulator.Core;
/// <summary>
/// 一个存档的「内容」那一半：卡牌定义 / Token 定义 / 区域定义 / 桌面主题。
///
/// 与 <c>state.json</c>（对局那一半）拆开是<b>用户已拍板的决定</b>，理由在 M5：
/// 运行时编辑器改的是"内容"，而它对局状态不需要动 ——
/// 两件事混在一个文件里，M5 每次改一个卡面数值都要重写整桌牌的位置。
///
/// <b>序列化的目标类型就是内存里那套定义类</b>（<c>CardDefinition</c> /
/// <c>TokenDefinition</c> / <c>ZoneDefinition</c>），不另建一套 DTO：
/// 另建等于多一份要同步维护的定义，而两边一旦分叉，
/// 症状是"存进去的东西读出来少了一半"。
/// </summary>
public sealed class SaveProject
{
	/// <summary>格式版本。读档时用它决定要不要迁移（现在只有 1）。</summary>
	public int FormatVersion { get; set; } = 1;

	public BoardTheme Board { get; set; } = new();

	public List<CardDefinition> Cards { get; set; } = new();

	public List<TokenDefinition> Tokens { get; set; } = new();

	/// <summary>
	/// 卡组（一副牌的配方，M5）。
	///
	/// <b>它是"内容"而不是"对局"</b>：卡组是"这副牌由什么组成"，
	/// 而"这 20 张牌现在分别在哪"属于 <c>state.json</c>。
	/// 放进这一半，M5 的「组卡组」才不需要碰对局状态。
	///
	/// 老存档（这个字段还不存在时写的）读进来得到空列表，
	/// 于是 <c>decks</c> 缺失不会让旧档炸 —— 只是"还没有卡组"。
	/// </summary>
	public List<CardDeck> Decks { get; set; } = new();

	/// <summary>区域定义，<b>次序即建区次序</b>（重叠命中取最后添加的，所以次序是语义）。</summary>
	public List<ZoneDefinition> Zones { get; set; } = new();

	public const int CurrentFormatVersion = 1;
}

/// <summary>
/// 一个存档的「对局」那一半：全部物件状态 + uid 计数器 + 区域成员次序。
///
/// <c>NextUidSeq</c> <b>必须存</b>：不存的话，存档 A 有 <c>card-0036</c>、删掉它、存档，
/// 再开一个存档并从 A 复制过来 —— 复制出来的物件会拿到 <c>card-0036</c>，
/// 而 A 里已经有一个。uid 撞车是最难查的一类 bug，因为两个物件在代码里长得完全一样。
///
/// <b><see cref="ZoneMembers"/> 同样必须存 —— 这条是自检逼出来的。</b>
/// 第一版只有物件的 <c>ZoneId</c>（那是"我属于哪个区域"的<b>声明</b>），
/// 于是读档后牌库的成员表是空的、16 张牌却自称在牌库里，
/// 一致性判红 <c>member_counts_match</c>。设计文档 §4.4 写的是
/// "读档时重建成员关系"，而<b>重建所需的次序只存在于区域自己身上</b>：
/// 谁是顶牌完全由 <c>Zone.Members</c> 的次序决定，物件身上查不到。
/// </summary>
public sealed class SaveState
{
	public int FormatVersion { get; set; } = 1;

	public int NextUidSeq { get; set; }

	/// <summary>全部物件，<b>顺序即绘制次序</b>（谁压谁）。</summary>
	public List<ObjectState> Objects { get; set; } = new();

	/// <summary>
	/// 区域 id → 成员 uid 次序（底 → 顶）。
	///
	/// 键是"这个区域当前有哪些成员"，次序是"谁是顶牌"。
	/// 用有序的 <c>List</c> 而不是集合：牌库的顺序就是玩法本身。
	/// </summary>
	public Dictionary<string, List<string>> ZoneMembers { get; set; } = new();

	public const int CurrentFormatVersion = 1;
}

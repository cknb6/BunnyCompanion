namespace BunnyCompanion.Engine;

/// <summary>
/// 鼠标手势 → 动作 + 台词 + 爱心。根据点击分区、拖拽方向/距离、连点档位多样化反馈。
/// </summary>
public enum BodyZone
{
    HeadLeft,
    HeadCenter,
    HeadRight,
    BodyLeft,
    BodyCenter,
    BodyRight,
    FootLeft,
    FootCenter,
    FootRight,
}

public enum DragDirection
{
    None,
    Left,
    Right,
    Up,
    Down,
}

public enum DragIntensity
{
    /// <summary>几乎没动，当作点击。</summary>
    None,
    /// <summary>轻拖。</summary>
    Soft,
    /// <summary>正常挪位置。</summary>
    Normal,
    /// <summary>甩一下 / 甩飞。</summary>
    Fling,
}

public sealed record MouseReaction(
    string ActionKey,
    string Message,
    int Affection,
    bool PlaySound = true);

public static class MouseReactionCatalog
{
    /// <summary>将点击坐标映射为 3×3 分区。</summary>
    public static BodyZone ResolveZone(double ratioX, double ratioY)
    {
        ratioX = Math.Clamp(ratioX, 0, 1);
        ratioY = Math.Clamp(ratioY, 0, 1);

        var col = ratioX < 0.33 ? 0 : ratioX > 0.67 ? 2 : 1;
        var row = ratioY < 0.36 ? 0 : ratioY > 0.74 ? 2 : 1;

        return (row, col) switch
        {
            (0, 0) => BodyZone.HeadLeft,
            (0, 1) => BodyZone.HeadCenter,
            (0, 2) => BodyZone.HeadRight,
            (1, 0) => BodyZone.BodyLeft,
            (1, 1) => BodyZone.BodyCenter,
            (1, 2) => BodyZone.BodyRight,
            (2, 0) => BodyZone.FootLeft,
            (2, 1) => BodyZone.FootCenter,
            _ => BodyZone.FootRight,
        };
    }

    public static DragDirection ResolveDragDirection(double deltaX, double deltaY)
    {
        if (Math.Abs(deltaX) < 1 && Math.Abs(deltaY) < 1)
            return DragDirection.None;
        return Math.Abs(deltaX) >= Math.Abs(deltaY)
            ? (deltaX >= 0 ? DragDirection.Right : DragDirection.Left)
            : (deltaY >= 0 ? DragDirection.Down : DragDirection.Up);
    }

    public static DragIntensity ResolveDragIntensity(double distancePx, double durationSec)
    {
        if (distancePx < 8)
            return DragIntensity.None;
        var speed = durationSec > 0.05 ? distancePx / durationSec : distancePx;
        if (distancePx > 280 || speed > 900)
            return DragIntensity.Fling;
        if (distancePx < 60)
            return DragIntensity.Soft;
        return DragIntensity.Normal;
    }

    public static MouseReaction PickClick(
        BodyZone zone,
        int rapidCount,
        int clickCount,
        string partnerName,
        string? avoidAction = null)
    {
        // 三连点彩蛋
        if (clickCount >= 3)
            return Pick(TripleClickPool(partnerName), avoidAction);

        // 连点档位
        if (rapidCount >= 8)
            return Pick(HyperClickPool(partnerName), avoidAction);
        if (rapidCount >= 5)
            return Pick(RapidClickPool(partnerName), avoidAction);

        return zone switch
        {
            BodyZone.HeadLeft => Pick(HeadLeftPool(partnerName), avoidAction),
            BodyZone.HeadCenter => Pick(HeadCenterPool(partnerName), avoidAction),
            BodyZone.HeadRight => Pick(HeadRightPool(partnerName), avoidAction),
            BodyZone.BodyLeft => Pick(BodyLeftPool(partnerName), avoidAction),
            BodyZone.BodyCenter => Pick(BodyCenterPool(partnerName), avoidAction),
            BodyZone.BodyRight => Pick(BodyRightPool(partnerName), avoidAction),
            BodyZone.FootLeft => Pick(FootLeftPool(partnerName), avoidAction),
            BodyZone.FootCenter => Pick(FootCenterPool(partnerName), avoidAction),
            BodyZone.FootRight => Pick(FootRightPool(partnerName), avoidAction),
            _ => Pick(BodyCenterPool(partnerName), avoidAction),
        };
    }

    public static MouseReaction PickDoubleClick(string partnerName, string? avoidAction = null) =>
        Pick(DoubleClickPool(partnerName), avoidAction);

    public static MouseReaction PickDragRelease(
        DragDirection dir,
        DragIntensity intensity,
        string partnerName,
        string? avoidAction = null)
    {
        if (intensity == DragIntensity.Fling)
            return Pick(FlingPool(dir, partnerName), avoidAction);
        if (intensity == DragIntensity.Soft)
            return Pick(SoftDragPool(dir, partnerName), avoidAction);

        return dir switch
        {
            DragDirection.Left => Pick(DragLeftPool(partnerName), avoidAction),
            DragDirection.Right => Pick(DragRightPool(partnerName), avoidAction),
            DragDirection.Up => Pick(DragUpPool(partnerName), avoidAction),
            DragDirection.Down => Pick(DragDownPool(partnerName), avoidAction),
            _ => Pick(DragNormalPool(partnerName), avoidAction),
        };
    }

    public static MouseReaction PickWheel(bool up, string partnerName) =>
        up
            ? Pick(WheelUpPool(partnerName), null)
            : Pick(WheelDownPool(partnerName), null);

    public static MouseReaction PickDragStart(string partnerName) =>
        Pick(DragStartPool(partnerName), null);

    // ---------- pools ----------

    private static MouseReaction[] HeadCenterPool(string n) =>
    [
        new("headpat", "再摸一下嘛，我很乖的。", 3),
        new("headpat", "头顶专属充电位，只给你开放～", 3),
        new("delighted", "被摸头的时候，心会软软的。", 3),
        new("shy", "耳朵尖都红了……继续也没关系。", 3),
        new("wink", $"摸摸合格，{n}今天也温柔。", 2),
        new("plush", "蹭蹭……再多摸两下也行。", 3),
        new("kiss", "摸头套餐附赠一个么么。", 4),
    ];

    private static MouseReaction[] HeadLeftPool(string n) =>
    [
        new("shy", "左耳朵有点敏感……轻一点呀。", 3),
        new("curious", "在找什么秘密吗？这边也有小心思。", 2),
        new("look_back", "你戳这边，我会偷偷看你。", 2),
        new("headpat", "这边也可以rua，很公平。", 3),
        new("bashful", $"被{n}摸左耳，会想粘着你。", 3),
    ];

    private static MouseReaction[] HeadRightPool(string n) =>
    [
        new("wink", "右耳朵收到信号：有人偏心我。", 2),
        new("headpat", "哼哼，右边也要一样多。", 3),
        new("laugh", "痒……你是故意的吧？", 2),
        new("shy", "这边被点，会不自觉歪头。", 2),
        new("heart", "耳朵到心的距离，比想象中短。", 3),
    ];

    private static MouseReaction[] BodyCenterPool(string n) =>
    [
        new("wave", $"嗨，{n}～点到心口了。", 2),
        new("plush", "抱抱位在这儿，签收一下。", 3),
        new("clap", "被点名的感觉，像被夸奖。", 2),
        new("curious", "你在忙什么？看起来好认真。", 1),
        new("kneel", "我坐好了，听你说。", 2),
        new("gift", "心里悄悄给你准备了小心意。", 3),
        new("music", "心口被点，节奏都乱了半拍。", 2),
        new("sit", "我就在这儿陪着你。", 1),
        new("flowers", "送你一束看不见的花。", 2),
    ];

    private static MouseReaction[] BodyLeftPool(string n) =>
    [
        new("look_back", "左边有风……是你在戳我吗？", 2),
        new("dance", "左半边先热身，要不要看我跳？", 2),
        new("tiptoe", "轻轻踮脚，贴近你一点。", 2),
        new("pout", "只戳左边，右边会吃醋的。", 2),
        new("wave", "从左边打招呼：嗨呀。", 1),
        new("curious", "左边视角也想看看你在干嘛。", 1),
    ];

    private static MouseReaction[] BodyRightPool(string n) =>
    [
        new("point", "右边点到了，是要我指路吗？", 2),
        new("dance", "右脚先动——一二三！", 2),
        new("clap", "右侧应援位已就位。", 2),
        new("shy", "被从右边靠近，会有点害羞。", 2),
        new("heart", "右心房也有你的名字。", 3),
        new("stretch", "伸个懒腰，你也活动一下吧。", 1),
    ];

    private static MouseReaction[] FootCenterPool(string n) =>
    [
        new("surprised", "嘿！脚底很痒啦～", 2),
        new("jump", "吓我一跳！……再来一次？", 2),
        new("pout", "踩脚掌犯规！（超小声：还可以）", 2),
        new("tiptoe", "脚尖踮起来，不给你乱点！", 2),
        new("laugh", "哈哈哈脚心禁区被攻破。", 2),
        new("annoyed", "哼，脚底抗议中。", 1),
    ];

    private static MouseReaction[] FootLeftPool(string n) =>
    [
        new("jump", "左脚弹射起步！", 2),
        new("surprised", "左脚！警告一次哦。", 2),
        new("pout", "左脚也是我的尊严。", 2),
        new("walk", "左脚痒了，想去走走。", 1),
        new("tiptoe", "左脚尖：请温柔对待。", 2),
    ];

    private static MouseReaction[] FootRightPool(string n) =>
    [
        new("jump", "右脚也要蹦一下才公平。", 2),
        new("surprised", "右脚收到骚扰警报。", 2),
        new("dance", "右脚先打拍子～", 2),
        new("land", "落地！脚还在抖。", 1),
        new("pout", "右脚：今天也想被好好放在地上。", 2),
    ];

    private static MouseReaction[] DoubleClickPool(string n) =>
    [
        new("heart", $"最喜欢{n}啦 ♥", 4),
        new("kiss", "双击签收一个亲亲。", 4),
        new("heart", "比心连发！接好。", 4),
        new("wink", "双击解锁：今日偏爱 +1。", 3),
        new("celebrate", "双击庆祝我们又腻歪了一秒。", 3),
        new("gift", "双击礼物盒：里面是喜欢。", 4),
    ];

    private static MouseReaction[] TripleClickPool(string n) =>
    [
        new("dance", "三连击！专属小舞时间～", 4),
        new("clap", "三连夸夸：你今天超棒。", 3),
        new("birthday", "三连像过节，我先庆祝起来。", 4),
        new("music", "三连节拍，跟着晃。", 3),
        new("laugh", "点太快啦，我都笑场了。", 3),
    ];

    private static MouseReaction[] RapidClickPool(string n) =>
    [
        new("laugh", "哈哈哈哈，你点得好认真！", 5),
        new("surprised", "停——让我缓两秒！", 3),
        new("dance", "连点成鼓点，我跳舞回应。", 4),
        new("pout", "点点点……是想把我点成星星吗？", 3),
    ];

    private static MouseReaction[] HyperClickPool(string n) =>
    [
        new("celebrate", "连点大师！爱心值爆炸中～", 6),
        new("laugh", "我要被你点笑岔气了！", 5),
        new("heart", $"停手奖励：超大比心给{n}。", 5),
        new("birthday", "这密集程度，值得开趴。", 5),
    ];

    private static MouseReaction[] DragStartPool(string n) =>
    [
        new("dragged", "咦，被拎起来了……", 1, false),
        new("dragged", "飞咯～抓紧你。", 1, false),
        new("dragged", "搬家中，请系好安全带。", 1, false),
        new("dragged", "哇，风景在动！", 1, false),
    ];

    private static MouseReaction[] SoftDragPool(DragDirection dir, string n) =>
    [
        new("recover", "轻轻挪一下就好，我站稳了。", 1),
        new("shy", "小步移动……有点害羞。", 2),
        new("sit", "放这儿吗？好，我不跑。", 1),
        new("wave", dir == DragDirection.Left ? "往左一点点，收到。" : "位置微调完成～", 1),
    ];

    private static MouseReaction[] DragLeftPool(string n) =>
    [
        new("look_back", "往左飞！回头看看原来的位置。", 2),
        new("wave", "左边的风景，也想分享给你。", 1),
        new("curious", "为什么带我去左边？有故事吗？", 2),
        new("recover", "左迁成功，落地打卡。", 1),
        new("tiptoe", "踮着脚落在左边。", 2),
    ];

    private static MouseReaction[] DragRightPool(string n) =>
    [
        new("point", "右边就位！要我帮你指点屏幕吗？", 2),
        new("wave", "右移完成，报到。", 1),
        new("clap", "换到右边，换个心情鼓掌。", 2),
        new("recover", "右岸登陆成功。", 1),
        new("curious", "右边有什么好东西？", 1),
    ];

    private static MouseReaction[] DragUpPool(string n) =>
    [
        new("jump", "被举高高！再高一点点～", 3),
        new("surprised", "海拔上升中……有点晕乎。", 2),
        new("stretch", "拉高视野，伸个懒腰。", 2),
        new("celebrate", "高处的我，宣布喜欢你。", 3),
        new("look_back", "从上面偷看你，嘿嘿。", 2),
    ];

    private static MouseReaction[] DragDownPool(string n) =>
    [
        new("land", "落地！脚踩实了。", 2),
        new("sit", "放低姿态，坐下来陪你。", 2),
        new("kneel", "蹲好了，听候差遣。", 2),
        new("recover", "下降完成，安全着地。", 1),
        new("sleepy", "放低一点，想靠着休息。", 2),
    ];

    private static MouseReaction[] DragNormalPool(string n) =>
    [
        new("recover", "新位置打卡，喜欢这里。", 1),
        new("wave", "搬家完毕，还是你的小挂件。", 1),
        new("curious", "这儿视野怎么样？", 1),
        new("sit", "安顿好了，继续陪你。", 1),
    ];

    private static MouseReaction[] FlingPool(DragDirection dir, string n) =>
    [
        new("surprised", "哇——甩太快啦！心脏在蹦。", 3),
        new("jump", "离心力！我还以为要飞出屏幕。", 3),
        new("laugh", "被甩飞的感觉……意外地开心？", 3),
        new("land", "急刹落地！头还在转。", 2),
        new("dizzy_spin", dir == DragDirection.Left || dir == DragDirection.Right
            ? "转晕了……抱一下我。"
            : "垂直甩飞体验卡已用完。", 3),
        new("pout", "粗暴搬运！要赔我一个亲亲。", 3),
    ];

    private static MouseReaction[] WheelUpPool(string n) =>
    [
        new("jump", "滚轮向上——我跳一下回应！", 2),
        new("stretch", "往上滚，精神也跟着拔高。", 1),
        new("clap", "滚轮点赞？那我鼓掌。", 2),
        new("look_back", "你在往上找什么呀？", 1),
    ];

    private static MouseReaction[] WheelDownPool(string n) =>
    [
        new("sit", "往下滚，我坐下等你。", 1),
        new("sleepy", "滚轮往下……有点想眯眼。", 2),
        new("kneel", "降低高度，靠近桌面。", 1),
        new("drink", "往下滚到喝水提醒？先干一杯。", 2),
    ];

    private static MouseReaction Pick(MouseReaction[] pool, string? avoidAction)
    {
        if (pool.Length == 0)
            return new MouseReaction("wave", "嗨～", 1);

        // 尽量不与上一次相同动作
        if (!string.IsNullOrEmpty(avoidAction) && pool.Length > 1)
        {
            var filtered = pool.Where(r => !r.ActionKey.Equals(avoidAction, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (filtered.Length > 0)
                pool = filtered;
        }

        return pool[Random.Shared.Next(pool.Length)];
    }
}

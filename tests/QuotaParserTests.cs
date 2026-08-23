using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using SafeCodexQuotaWidget;

internal static class QuotaParserTests
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    private static int Main()
    {
        try
        {
            ExtraModelFallback();
            SecondaryWinsOverModelLimit();
            ModelLimitCannotBecomeMainQuota();
            EmptySecondaryFallsBackToModelLimit();
            UnnamedModelLimitIsIgnored();
            LowestModelLimitIsDeterministic();
            LegacyMainQuotaStillGetsModelExtra();
            InvalidUsedPercentDoesNotCreateAWindow();
            EqualModelLimitsUseStableIdOrdering();
            PlanMultiplierFormatting();
            Console.WriteLine("Quota parser tests passed: 10");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void ExtraModelFallback()
    {
        QuotaSnapshot value = Parse(Wrap(
            "\"codex\":" + Limit("codex", null, Window(56, 10080), "null") + "," +
            "\"codex_bengalfox\":" + Limit("codex_bengalfox", "GPT-5.3-Codex-Spark", Window(0, 10080), "null")));
        Assert(value.Primary.RemainingPercent == 44, "Main quota should remain 44%.");
        Assert(value.Extra != null && value.Extra.Kind == ExtraQuotaKind.ModelLimit,
            "Spark should be selected as a model extra.");
        Assert(value.Extra.Window.RemainingPercent == 100, "Spark extra should be 100%.");
    }

    private static void SecondaryWinsOverModelLimit()
    {
        QuotaSnapshot value = Parse(Wrap(
            "\"codex\":" + Limit("codex", null, Window(56, 10080), Window(80, 300)) + "," +
            "\"spark\":" + Limit("spark", "Spark", Window(95, 10080), "null")));
        Assert(value.Extra.Kind == ExtraQuotaKind.SecondaryWindow,
            "Codex secondary must win over a lower model-specific limit.");
        Assert(value.Extra.Window.RemainingPercent == 20, "Secondary remaining should be 20%.");
    }

    private static void ModelLimitCannotBecomeMainQuota()
    {
        bool threw = false;
        try
        {
            Parse(Wrap("\"spark\":" + Limit("spark", "Spark", Window(0, 10080), "null")));
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Assert(threw, "A model-specific limit must never be promoted to the main quota.");
    }

    private static void EmptySecondaryFallsBackToModelLimit()
    {
        QuotaSnapshot value = Parse(Wrap(
            "\"codex\":" + Limit("codex", null, Window(40, 10080), "{}") + "," +
            "\"spark\":" + Limit("spark", "Spark", Window(10, 10080), "null")));
        Assert(value.Secondary == null, "An empty secondary object must not become fake 100%.");
        Assert(value.Extra != null && value.Extra.Kind == ExtraQuotaKind.ModelLimit,
            "An empty secondary should allow the model fallback.");
    }

    private static void UnnamedModelLimitIsIgnored()
    {
        QuotaSnapshot value = Parse(Wrap(
            "\"codex\":" + Limit("codex", null, Window(40, 10080), "null") + "," +
            "\"mystery\":" + Limit("mystery", null, Window(10, 10080), "null")));
        Assert(value.Extra == null, "Unnamed independent limits should not be shown as user-facing extras.");
    }

    private static void LowestModelLimitIsDeterministic()
    {
        QuotaSnapshot value = Parse(Wrap(
            "\"codex\":" + Limit("codex", null, Window(40, 10080), "null") + "," +
            "\"zeta\":" + Limit("zeta", "Zeta", Window(20, 10080), "null") + "," +
            "\"alpha\":" + Limit("alpha", "Alpha", Window(80, 10080), "null")));
        Assert(value.Extra != null && value.Extra.LimitId == "alpha",
            "The lowest remaining independent model limit should be selected.");
    }

    private static void LegacyMainQuotaStillGetsModelExtra()
    {
        string legacy = Limit("codex", null, Window(30, 10080), "null");
        string model = Limit("spark", "Spark", Window(0, 10080), "null");
        QuotaSnapshot value = Parse("{\"rateLimits\":" + legacy +
            ",\"rateLimitsByLimitId\":{\"spark\":" + model + "}}");
        Assert(value.Primary != null && value.Primary.RemainingPercent == 70,
            "A valid legacy Codex limit should remain the main quota.");
        Assert(value.Extra != null && value.Extra.LimitId == "spark",
            "A model limit should still be available beside a legacy main quota.");
    }

    private static void InvalidUsedPercentDoesNotCreateAWindow()
    {
        string invalid = "{\"limitId\":\"codex\",\"planType\":\"prolite\"," +
                         "\"primary\":{\"usedPercent\":\"invalid\",\"windowDurationMins\":10080}," +
                         "\"secondary\":null}";
        QuotaSnapshot value = Parse(Wrap("\"codex\":" + invalid));
        Assert(value.Primary == null, "An invalid usedPercent must not create a quota window.");
    }

    private static void EqualModelLimitsUseStableIdOrdering()
    {
        QuotaSnapshot value = Parse(Wrap(
            "\"codex\":" + Limit("codex", null, Window(40, 10080), "null") + "," +
            "\"zeta\":" + Limit("zeta", "Zeta", Window(50, 10080), "null") + "," +
            "\"alpha\":" + Limit("alpha", "Alpha", Window(50, 10080), "null")));
        Assert(value.Extra != null && value.Extra.LimitId == "alpha",
            "Equal model limits should be ordered by limit id.");
    }

    private static void PlanMultiplierFormatting()
    {
        Assert(PlanDisplayFormatter.Format("FREE") == "Free",
            "Free must use the official user-facing plan name.");
        Assert(PlanDisplayFormatter.Format("go") == "Go",
            "Go must use the official user-facing plan name.");
        Assert(PlanDisplayFormatter.Format("plus") == "Plus",
            "Plus must use the official user-facing plan name.");
        Assert(PlanDisplayFormatter.Format("prolite") == "Pro 5×",
            "Pro Lite must be displayed as the Pro 5x tier.");
        Assert(PlanDisplayFormatter.Format("PRO") == "Pro 20×",
            "Pro must be displayed as the Pro 20x tier.");
        Assert(PlanDisplayFormatter.Format("business") == "BUSINESS",
            "Other plan types must preserve the existing fallback formatting.");
        Assert(PlanDisplayFormatter.Format("unknown") == "未知",
            "An explicitly unknown plan type must use the localized fallback.");
        Assert(PlanDisplayFormatter.Format(null) == "未知",
            "A missing plan type must remain unknown.");
    }

    private static QuotaSnapshot Parse(string json)
    {
        Dictionary<string, object> result = Json.DeserializeObject(json) as Dictionary<string, object>;
        MethodInfo method = typeof(CodexQuotaClient).GetMethod("ParseSnapshot",
            BindingFlags.NonPublic | BindingFlags.Static);
        try
        {
            return (QuotaSnapshot)method.Invoke(null, new object[] { result });
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException;
        }
    }

    private static string Wrap(string limits)
    {
        return "{\"rateLimitsByLimitId\":{" + limits + "}}";
    }

    private static string Limit(string id, string name, string primary, string secondary)
    {
        string encodedName = name == null ? "null" : Json.Serialize(name);
        return "{\"limitId\":" + Json.Serialize(id) + ",\"limitName\":" + encodedName +
               ",\"planType\":\"prolite\",\"primary\":" + primary +
               ",\"secondary\":" + secondary + "}";
    }

    private static string Window(int used, int minutes)
    {
        return "{\"usedPercent\":" + used + ",\"windowDurationMins\":" + minutes +
               ",\"resetsAt\":2000000000}";
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using Sangam.Identity.Domain;
using Sangam.Identity.Infrastructure.Customisation;

namespace Sangam.Identity.Infrastructure.Tests.Customisation;

/// <summary>PR-19: the built-in texts, filling, checking and the fixed HTML layout.</summary>
public sealed class TemplateTextTests
{
    private static readonly Dictionary<string, string> Values = new() { ["name"] = "Asha Menon", ["code"] = "123456", ["minutes"] = "10" };

    [Fact]
    public void TheEnglishVerificationEmail_IsTheOneSangamAlwaysSent()
    {
        (string subject, string body) = DefaultTemplates.Find(MessageTemplateKinds.EmailVerification, "en-IN")!.Value;
        Assert.Equal("123456 is your Sangam verification code", TemplateText.Fill(subject, Values));
        Assert.Equal(
            "Hello Asha Menon,\n\nYour Sangam verification code is:\n\n    123456\n\nEnter it on the page where you created your account. It is valid for 10 minutes and can be used once.\n\n"
            + "If you did not create a Sangam account, ignore this email; nothing will happen.\n\nSangam - one identity, many homes\nid.sangamid.in",
            TemplateText.Fill(body, Values));
    }

    [Fact]
    public void ASubject_StaysOnOneLine_WhateverIsFilledIntoIt()
    {
        // R7 (ASVS V5.2.3): a name with a line break must not start a new mail header.
        Dictionary<string, string> values = new() { ["name"] = "Asha\r\nBcc: someone@example.in", ["code"] = "123456", ["minutes"] = "10" };
        string subject = TemplateText.FillSubject("Hello {{name}}", values);
        Assert.DoesNotContain('\r', subject);
        Assert.DoesNotContain('\n', subject);
        Assert.Equal("Hello Asha  Bcc: someone@example.in", subject);
        Assert.NotNull(TemplateText.CheckEmail(MessageTemplateKinds.All.First(k => !k.Sms), "Line one\rline two", "Body {code}"));
    }

    [Fact]
    public void EveryKind_HasABuiltInText_InEnglishHindiAndMalayalam_WithTheSameVariables()
    {
        foreach (MessageTemplateKind kind in MessageTemplateKinds.All.Where(k => !k.Sms))
        {
            (string enSubject, string enBody) = DefaultTemplates.Find(kind.Code, "en-IN")!.Value;
            IReadOnlySet<string> english = TemplateText.Variables(enSubject + enBody);
            Assert.Null(TemplateText.CheckEmail(kind, enSubject, enBody));
            foreach (string language in new[] { "hi-IN", "ml-IN" })
            {
                (string subject, string body) = DefaultTemplates.Find(kind.Code, language)!.Value;
                Assert.True(english.SetEquals(TemplateText.Variables(subject + body)), $"{kind.Code} {language}");
                Assert.Null(TemplateText.CheckEmail(kind, subject, body));
                Assert.NotEqual(enBody, body);
            }
        }
    }

    [Theory]
    [InlineData("Your code is {{code}}", "Code", null)]
    [InlineData("Your code is {{password}}", "Code", "“password” is not a variable")]
    [InlineData("No code here", "Code", "must contain the variable “code”")]
    [InlineData("{{code}}", "Line one\nline two", "one line")]
    [InlineData("{{code}}", "", "Enter both")]
    public void AnEmailTemplate_IsChecked(string body, string subject, string? problem)
    {
        string? result = TemplateText.CheckEmail(MessageTemplateKinds.Find(MessageTemplateKinds.SignInCode)!, subject, body);
        if (problem is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Contains(problem, result, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("{#var#} आपका SangamID साइन-इन कोड है। - SANGAM", "1107161234567890123", null)]
    [InlineData("{#var#} आपका कोड {#var#}", "1107161234567890123", "exactly 1 variable markers")]
    [InlineData("{#var#} code", "", "DLT template id")]
    [InlineData("{#var#} code", "ABC", "DLT template id")]
    public void AnSmsTemplate_NeedsItsDltIdAndTheRegisteredVariables(string body, string dlt, string? problem)
    {
        string? result = TemplateText.CheckSms(body, dlt, "{#var#} is your SangamID sign-in code. It expires in 10 minutes. Do not share it. - SANGAM");
        if (problem is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Contains(problem, result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheHtml_KeepsSangamsLayout_AndEncodesEverythingAPartnerWrote()
    {
        Dictionary<string, string> values = new() { ["code"] = "123456", ["name"] = "<script>alert(1)</script>" };
        string html = TemplateText.Html("Hello <b>{{name}}</b>\n\n    123456\n\nBye", values, "LiPi <HIS>", "#1D4E89", "hi-IN");
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;", html, StringComparison.Ordinal);
        Assert.Contains("LiPi &lt;HIS&gt;", html, StringComparison.Ordinal);
        Assert.Contains("letter-spacing:0.2em\">123456</p>", html, StringComparison.Ordinal);
        Assert.Contains("lang=\"hi-IN\"", html, StringComparison.Ordinal);
        Assert.Contains("background:#1D4E89", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#0F3B38", true)]
    [InlineData("#1D4E89", true)]
    [InlineData("#FFD700", false)]
    [InlineData("#F5F3EE", false)]
    public void AnAccent_MustCarryWhiteText(string colour, bool readable)
        => Assert.Equal(readable, EfCustomisationService.ContrastWithWhite(colour) >= 4.5);
}

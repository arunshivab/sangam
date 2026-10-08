using Microsoft.Extensions.Localization;
using Sangam.Identity.Domain.Enums;
using Sangam.Web.Shared.Localization;

namespace Sangam.Admin.Web.Components.Common;

/// <summary>Words for the grievance log's fixed lists (D-D), in the reader's language.</summary>
public static class GrievanceText
{
    private static IStringLocalizer L => CatalogueStringLocalizer.Shared;

    /// <summary>How a grievance arrived.</summary>
    /// <param name="channel">The channel code.</param>
    public static string Channel(string channel)
    {
        return channel switch
        {
            "email" => L["E-mail"],
            "letter" => L["Letter"],
            "phone" => L["Phone call"],
            "in_person" => L["In person"],
            _ => L["Other"],
        };
    }

    /// <summary>What a grievance is about.</summary>
    /// <param name="category">The category code.</param>
    public static string Category(string category)
    {
        return category switch
        {
            "access" => L["Access to their data"],
            "correction" => L["Correcting their data"],
            "erasure" => L["Erasing their data"],
            "consent" => L["Consent"],
            "security" => L["Security of their data"],
            "account" => L["Their account"],
            _ => L["Other"],
        };
    }

    /// <summary>Where a grievance stands.</summary>
    /// <param name="status">The status.</param>
    public static string Status(GrievanceStatus status)
    {
        return status switch
        {
            GrievanceStatus.Received => L["received, not yet acknowledged"],
            GrievanceStatus.Acknowledged => L["acknowledged"],
            GrievanceStatus.Resolved => L["resolved"],
            _ => L["declined"],
        };
    }

    /// <summary>A step in a grievance's history.</summary>
    /// <param name="kind">The step's kind.</param>
    public static string Step(string kind)
    {
        return kind switch
        {
            "logged" => L["Logged"],
            "acknowledged" => L["Acknowledged"],
            "note" => L["Note"],
            "resolved" => L["Resolved"],
            _ => L["Declined"],
        };
    }
}

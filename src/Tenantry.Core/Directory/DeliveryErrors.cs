using System.Text.RegularExpressions;

namespace Tenantry.Core.Directory;

/// <summary>A delivery error in words: what the code means and what an admin can do about it.</summary>
public sealed record DeliveryProblem(string Code, string Meaning, string WhatToDo, bool Temporary);

/// <summary>
/// Turns the enhanced status codes in a message trace step or bounce ("550 5.1.10 Recipient not
/// found") into words, from Microsoft's list of Exchange Online non-delivery codes. A code that
/// isn't on the list still says whether Exchange will keep trying.
/// </summary>
public static partial class DeliveryErrors
{
    [GeneratedRegex(@"\b([245])\.(\d{1,3})\.(\d{1,3})\b")]
    private static partial Regex Code();

    /// <summary>The first enhanced status code in <paramref name="text"/>, explained; null when there is none.</summary>
    public static DeliveryProblem? Explain(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        // Exchange drops some messages on purpose and says so with a success code: name those first
        // (seen live: most "Failed" trace results were automatic replies not sent).
        if (Resolver(text) is { } dropped)
            return dropped;
        if (Code().Match(text) is not { Success: true } m)
            return null;
        var (klass, subject, detail) = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        if (klass == 2)
            return null;
        var code = m.Value;
        var temporary = klass == 4;
        if (Known(klass, subject, detail) is { } known)
            return new DeliveryProblem(code, known.Meaning, known.WhatToDo, temporary);
        return temporary
            ? new DeliveryProblem(code, "A temporary problem: Exchange keeps trying for up to a day before giving up.", "Usually nothing. If it keeps happening, look at the receiving side's mail servers.", true)
            : new DeliveryProblem(code, "Delivery failed for good, and the sender got a bounce message.", "Read the server's message beside the code; it usually names the cause.", false);
    }

    /// <summary>The messages Exchange dropped on purpose, named by the reason it records.</summary>
    static DeliveryProblem? Resolver(string text)
    {
        if (text.Contains("RESOLVER.OOF.IntToExt", StringComparison.OrdinalIgnoreCase))
            return new("Automatic reply", "An out-of-office reply that Exchange didn't send outside the organization: the person's automatic reply is for people inside only, or replies to that domain are turned off.",
                "Nothing, unless outside senders should get it: then set the person's automatic reply to answer people outside too, or allow automatic replies in Remote Domains.", false);
        if (text.Contains("RESOLVER.OOF.ExtToInt", StringComparison.OrdinalIgnoreCase))
            return new("Automatic reply", "An outside sender's out-of-office reply that Exchange didn't pass on to the person here.",
                "Nothing: automatic replies from outside are dropped on purpose.", false);
        if (text.Contains("RESOLVER.MSGTYPE.AF", StringComparison.OrdinalIgnoreCase))
            return new("Automatic forwarding", "A message that would have been forwarded automatically outside the organization, which isn't allowed.",
                "If it should be allowed, turn on automatic forwarding for that person in an outbound spam policy, or for that domain in Remote Domains.", false);
        return null;
    }

    static (string Meaning, string WhatToDo)? Known(int k, int s, int d) => (k, s, d) switch
    {
        (4, 4, 7) => ("The receiving server didn't accept it in time, so it expired in the queue.",
            "Usually the receiving side. Check their mail (MX) records and servers; the trace shows each retry."),
        (4, 4, 316) => ("The receiving server refused the connection.", "A network problem on their side. Wait, or check their MX record."),
        (4, 5, 3) => ("Too many recipients at one domain in one message (more than 200).", "The sending app has to split the recipients into smaller groups."),
        (4, 7, >= 500 and <= 699) or (4, 7, >= 850 and <= 899) => ("Sending is held back for now because of suspicious activity.",
            "It usually clears by itself. If it keeps happening, check for a hacked account or a misused relay."),
        (4, 7, 26) or (5, 7, 25) => ("Sent over IPv6 without passing SPF or DKIM, or from a server with no reverse DNS.",
            "The sender fixes their SPF or DKIM, or their server's reverse DNS (PTR) record."),
        (5, 1, 0) => ("The sender was refused, often when replying from an old or offline copy of a message.", "Send it again from a new message."),
        (5, 1, 1) => ("The address doesn't exist: a typo, a deleted mailbox, or an old entry in the sender's autocomplete.",
            "Check the address. Remove the old entry from the sender's autocomplete. After a migration, add the old X500 address to the mailbox."),
        (5, 1, 8) => ("The sender's account is blocked from sending because it sent spam: usually a hacked account.",
            "Secure the account (reset the password, sign it out everywhere), then unblock it in Blocked Senders."),
        (5, 1, 10) => ("The recipient wasn't found.", "Check the address. For someone in your organization, check their mailbox exists and has synced."),
        (5, 1, 90) or (5, 2, 2) => ("The sender went over the limit of recipients or messages they can send.",
            "Check whether the account was hacked. Newsletters belong in a bulk-mail service."),
        (5, 2, 121) or (5, 2, 122) => ("The recipient gets more mail an hour than Exchange allows.", "The sending system needs to slow down."),
        (5, 4, 1) => ("The receiving side rejected the address, or refused to relay it.", "Check the recipient exists, and your accepted domains and MX record."),
        (5, 4, 14) or (5, 4, 6) => ("A routing loop: the message went round in circles.", "Look for forwarding, connectors or accepted-domain settings that send mail back and forth."),
        (5, 7, 1) => ("Not allowed: the recipient or group doesn't accept mail from this sender, or the sender couldn't relay.",
            "Check the group's delivery restrictions, the relay connector, or the device's sign-in to send mail."),
        (5, 7, 12) or (5, 7, >= 133 and <= 136) => ("The recipient only accepts mail from inside the organization.",
            "If outside senders should reach it, turn off \"only accept from people in the organization\" for that mailbox or group."),
        (5, 7, 23) => ("The sender failed SPF: their domain doesn't allow the server that sent it.", "The sending domain fixes its SPF record."),
        (5, 7, 57) => ("A device or app tried to send without signing in.", "Fix the device's or app's sending settings (SMTP sign-in), or use a connector for it."),
        (5, 7, 64) => ("Relay refused: the inbound connector didn't recognize the sending server.", "Check the connector's certificate or IP addresses."),
        (5, 7, 124) => ("The sender isn't on the group's list of people allowed to send to it.", "Add them to the group's allowed senders, or loosen the restriction."),
        (5, 7, 367) => ("Forwarded mail failed the receiving side's SPF or DKIM checks.", "Common with outside filtering services; review how the mail is forwarded."),
        (5, 7, >= 501 and <= 503) => ("The account is banned from sending because it sent spam.", "Secure the account, reset its password, then ask Microsoft support to lift the ban."),
        (5, 7, 509) => ("The sender's domain fails DMARC, and its DMARC policy says to reject such mail.", "The sender's admin fixes SPF or DKIM so they line up with the address people see."),
        (5, 7, 511) => ("The sending server's address is banned.", "Ask Microsoft to delist it through its delist portal."),
        (5, 7, 520) => ("Forwarding outside the organization is blocked by the outbound spam policy.",
            "Allow automatic forwarding for those people in an outbound spam policy, if it's meant to happen."),
        (5, 7, 705) or (5, 7, 708) => ("Your organization went over its sending limits and is banned from sending for now.",
            "Find and secure hacked accounts or an open relay, then contact Microsoft support."),
        (5, 7, 750) => ("Mail was sent from a domain your organization hasn't added.", "Add and verify the domain as an accepted domain."),
        _ => null,
    };
}

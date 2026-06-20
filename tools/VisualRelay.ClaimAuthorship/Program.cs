using System.Text.RegularExpressions;
using VisualRelay.Core.Authorship;
using VisualRelay.Core.Execution;

// VisualRelay.ClaimAuthorship — the C# home of the former `me.sh`.
//
// Claims author + committer on the last N commits of the current branch (so
// both become the chosen identity, preserving author dates) AND strips any
// commit-message trailer mentioning "Claude". Operates on the repository at the
// current working directory.
//
// Usage: VisualRelay.ClaimAuthorship [-N]
//   -N            commits back from HEAD to consider (default: 5)
//   CLAIM_EMAIL   env override for the claim email (must contain '@')
//   CLAIM_NAME    env override for the claim name (defaults to CLAIM_EMAIL's
//                 local-part when only the email is set)

const int usageExit = 64;
const string usage = "usage: VisualRelay.ClaimAuthorship [-N]   (N = commits back from HEAD, default 5)";

if (args.Length > 1)
    return Fail(usage, usageExit);

var claimCount = 5;
if (args.Length == 1)
{
    if (!Regex.IsMatch(args[0], "^-[1-9][0-9]*$"))
        return Fail(usage, usageExit);
    claimCount = int.Parse(args[0].AsSpan(1));
}

var claimEmail = Environment.GetEnvironmentVariable("CLAIM_EMAIL");
var claimName = Environment.GetEnvironmentVariable("CLAIM_NAME");
var repoRoot = Directory.GetCurrentDirectory();

var claimer = new AuthorshipClaimer(new GitInvoker());
ClaimOutcome outcome;
try
{
    outcome = await claimer.ClaimAsync(repoRoot, claimCount, claimEmail, claimName, CancellationToken.None);
}
catch (Exception ex)
{
    return Fail($"VisualRelay.ClaimAuthorship: {ex.Message}", 1);
}

if (!outcome.Success)
    return Fail($"VisualRelay.ClaimAuthorship: {outcome.Error}", outcome.IsUsageError ? usageExit : 1);

Console.WriteLine(outcome.Rewrote
    ? $"Claimed authorship on {outcome.RewrittenCount} commit(s) and stripped Claude trailers."
    : "Nothing to do — range already claimed and Claude-trailer-free.");
return 0;

static int Fail(string message, int code)
{
    Console.Error.WriteLine(message);
    return code;
}

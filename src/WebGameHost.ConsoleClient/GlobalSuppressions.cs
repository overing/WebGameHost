
using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Reliability",
    "CA2007: Consider calling ConfigureAwait on the awaited task",
    Justification = "對於明確未使用 SynchronizationContext 的執行環境要求 ConfigureAwait 是多餘的; ref: https://devblogs.microsoft.com/dotnet/configureawait-faq/")]

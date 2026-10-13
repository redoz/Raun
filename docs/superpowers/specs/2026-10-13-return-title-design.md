# Return-aware step titles

Approved in conversation: `$return:id ?? name` (the format belongs to the returned value, not the fallback).

A producer title can use `[StepName("Given patient {$return:id ?? name} can reach the API")]`.
During discovery/execution, or after a failure, null output, skip or cancellation, `name` supplies the title part.
After a successful non-null return, that part becomes a typed reference to the actual returned value, with its recording-time raw snapshot.
Existing parameter title parts remain exactly as captured before the start observer/body. Never re-read input objects during completion.

## Syntax and semantics

- Reserved root `$return`, optional `:format`, optional `?? parameter[.member.path][:fallbackFormat]`.
- No arbitrary C# expressions and no chained coalescing. Format strings retain existing colon handling (e.g. `HH:mm`).
- Without a fallback, the unavailable return part is the plain ellipsis `…`.
- Null coalescing is based on the returned object, not whether its text is empty. Zero/false are available outputs.
- Unwrap the operation's declared Task<T>/ValueTask<T> result type. Reject result-less operations via existing RAUN008.
- Fallbacks cannot start at a `params` parameter (including member paths and explicit-array calls); RAUN008 explains this limitation. Ordinary parameter placeholders retain their existing behavior.
- Keep existing member-path access checks and format checks for fallback/return types. Root `$return.Member` is outside this slice.
- Generator output uses SyntaxFactory only, never ParseExpression/ParseStatement/ParseText.

## Seam

Add optional `ScenarioNode.FormatCompletedTitleMessage` with signature
`Func<StepMessage, object?, MessageFormattingContext, StepMessage>?`.
The generator records return-hole part indices and expected pre-title part count, then emits a static formatter that copies frozen parts and replaces only return holes.
For null output or unexpected pre-title shape, keep the original title. Cast boxed output to its declared result type before typed formatting.
Runtime invokes completion after successful Invoke. Formatter failures preserve the original title and never change the outcome.
If later branch selection turns provisional success into failure, restore the starting title.
Start/context/span names remain starting evidence. Terminal MTP/report name may use the completed title with stable test UID.

## Alternatives rejected

Making names such as Bob input resources misrepresents a producer's return and changes declared resource access.
Guessing links by string/key equality is ambiguous. Reformatting the whole title changes input snapshots after mutation.
A general expression language adds unnecessary grammar and evaluation scope.

## Verification

Generator grammar/analyzer/emission tests; runtime completion/fallback/snapshot tests; MTP stable identity; full solution plus report JS checks.
Real Aspire sample: Bob and Alice in Given titles link to the returned Patient and share identity with later When/Then references.

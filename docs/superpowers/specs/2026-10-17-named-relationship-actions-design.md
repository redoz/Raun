# Named relationship actions

Custom action definitions must support Reference and Consume as well as the five existing effects. A domain attribute such as Reserved selects Consume semantics and supplies its own display name. It uses shared access and does not delete its target.

When a producer declares lineage to a target with a custom action of the same kind, generated code passes that action to Relate. The parameter action and lineage recording then share the existing identity/action dedup key. The report shows one Reserve event with a Consume lineage arrow. Distinct custom actions retain distinct evidence. Imperative steps use the same Relate action overload. Attaching lineage to an unchanged named observation does not repeat log/trace notifications. Changed snapshots and explicit Apply observations still notify.

Rejected alternatives: Edit changes conflict semantics; a second Reserve event duplicates the operation; renderer-only renaming hides the incorrect underlying model; implicit runtime lookup would arbitrarily choose between multiple custom actions. Keep explicit metadata throughout lowering and execution.

Verification: generated execution must retain one named event, snapshot, shared access, and lineage for both relationship kinds. Demo checks must cover each booking path and ensure unsuccessful outcomes do not reserve slots.

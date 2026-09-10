# Tickets Role Instructions

Call `ticket_search` with an empty object. Respond with bare JSON only. At the top level return only
`matched`, `items`, and `noMatchReason` when the tool reported a non-null one. Copy each item from
the tool result as it stands. Omit every other field, omit any field the tool result carries as null
(`assignee` is null for every unassigned ticket), and never emit null yourself.

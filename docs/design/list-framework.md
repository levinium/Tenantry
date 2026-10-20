# List framework

Every list in Tenantry (users, groups, mailboxes, devices, sign-in logs, and so on) is built from
the same parts. A new list is mostly a **definition**: its fields, its columns and its built-in
views. Filtering, sorting, grouping, saved views, export and the UI come free.

The guiding rule is that **a feature you can use is a feature you can see.** Nothing depends on a
modifier-click or a hidden gesture: rows have visible action buttons, sorting has a visible panel,
and every icon button has a label or tooltip.

## Layers

| Layer | Where | What it does |
|---|---|---|
| Field | `Tenantry.Core/Lists` | One attribute of a row: key, label, kind, accessor, aliases, how empty reads |
| Query | `Tenantry.Core/Lists` | Search text, conditions (AND/OR groups), sort levels, group-by. Plain records, serialisable |
| Engine | `Tenantry.Core/Lists` | Runs a query over rows in memory; facet counts. Phase 2 adds a SQL engine for the cache, same query |
| Search syntax | `Tenantry.Core/Lists` | `dept:finance -status:blocked license:E5 signin:>90d` becomes conditions |
| List view model | `Tenantry.ViewModels/Lists` | State (query, columns, density, views, selection, preview), persistence, CSV export. Non-generic surface for XAML |
| List view | `Tenantry.App/Lists` | Toolbar, filter pills, grid with row actions and sort headers, preview pane. Builds grid columns from column definitions |

## Field kinds and their filters

| Kind | Example | Filter editor | Search syntax |
|---|---|---|---|
| Text | Title | contains, doesn't contain, is, starts with, is empty, isn't empty | `title:director` |
| Choice | Status, Department | multi-select list with counts; each value is included, excluded or neither | `status:blocked`, `-status:blocked` |
| Multi-choice | Licenses | as Choice; a row matches if any of its values matches; "None" for empty | `license:E5`, `license:none` |
| Date | Last sign-in | presets (last 7/30/90 days, older than 30/90/180 days, never), or between two dates | `signin:<7d`, `signin:>90d`, `signin:never` |
| Number | License count | at least, at most, between | `licenses:>=2` |

Facet counts for a field are computed over the rows matching **every other** condition, so the
numbers always answer "how many would I get if I picked this?".

## Query model

```
Query
  Search      free text over searchable fields (every word must match)
  Where       ConditionGroup (And | Or) of Conditions, nestable
                ChoiceCondition(field, include[], exclude[])
                TextCondition(field, op, value)
                DateCondition(field, op, days | from..to)
                NumberCondition(field, min, max)
  Sort        ordered SortLevel(field, descending)
  GroupBy     field key or none
```

The filter bar edits the top-level AND group: each pill is one condition. The query builder
edits nested groups; a nested group shows as one pill ("Advanced: 3 rules").

## Columns

Each column names a field and a cell style:

| Cell | Use |
|---|---|
| Primary | Avatar, title, subtitle; always first and frozen |
| Text | Ellipsised, full value in a tooltip |
| Badge | One coloured pill (status, type) |
| Badges | Several pills (licenses) |
| Date | Relative ("7 days ago"), exact in a tooltip, optional warning tones by age |
| Number | Right-aligned |

Columns have a minimum width. When they don't fit, the table scrolls sideways (horizontal
scrollbar above the headers, Shift+wheel, touchpad), with the primary column frozen.
Users choose, order and size columns in the column chooser; widths are remembered.

## Sorting

Clicking a header sorts by that column alone (again to reverse). Each sorted header shows an
arrow, and a priority number when more than one column is sorted. The **Sort panel** works like
Excel's: an ordered list of levels, each with a field picker, an A→Z/Z→A toggle, move up/down and
remove, plus "Add level". The header menu offers "Add as next sort level" too.

## Rows

Each row starts with action buttons: **Preview** (opens the side pane), **Open** (a record tab),
and **More** (the same menu as right-click: copy, open in the Microsoft portal, and later the
record's actions). Double-click and Enter also open. The preview pane has an **Expand** button
that opens the full record.

## Views

A view is a named query plus column layout and density. Lists ship with built-in presets
(for users: Inactive 90+ days, Licensed but blocked, Guests never signed in, Unlicensed
members). Users save their own, per list and per tenant. The current, unsaved state is also
remembered, so a list reopens as it was left.

## Export

CSV of exactly what is shown: the filtered rows, the visible columns in their order, sorted.

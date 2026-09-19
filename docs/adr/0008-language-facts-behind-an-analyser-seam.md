# Language facts are resolved per file, behind a seam a real parser can implement

Everything this server knows about a programming language is resolved from a file's extension and
answered by one component, `ILanguageAnalyzer`, in the `Language/` module. Callers ask it questions
and never read its data.

The questions are:

- Where does a file begin, and where does it stand after this line?
- What is the lexical state at this position — code, comment, string, or not established?
- What, if anything, does this line declare, and is it a declaration or an implementation?
- What does this file import?
- Is this file generated?
- What does every appearance of an identifier on this line look like — a write, a call, a read, a
  type use?

Every answer is an `Answer<T>`, which carries the value and the `Evidence` it was reached by:
`Text` for one read from the source, `Parsed` for one a real parser produced. It is on the answer
and not on the analyser, because an analyser that parses what it can and falls back on the rest
tells the truth only per answer.

The first is a question about the file and the rest about one line of it, which is the division the
scan is built on (#53). An analyser hands out an opaque `FilePosition` and takes one back, so a
caller walks a file's lines in order and asks about the ones it cares about; what is inside a
position is the implementation's — a text profile keeps the comments and literals still open, and a
parser would keep a node. A caller that cannot read the lines above a match passes
`FilePosition.Unknown` and is told `Lexical.Unknown` rather than handed a guess, which is what keeps
a bounded scan honest about where it stopped.

Each question is asked about as much text as decides it. Classification takes a whole line and
returns every appearance on it, because most of what decides an appearance — where the line's
comments and literals are, what it declares — is a fact about the line, and an implementation asked
one position at a time must either recompute that per appearance or keep state it cannot keep, since
one analyser answers every search at once. Asked per position, a 640 KB line naming an identifier
20,000 times cost 20,000 walks of it; asked per line it costs one.

## Why questions and not tables

A caller that asks "what are this language's comment prefixes" and then scans for them itself has
hardcoded that a regex is how the answer is found. No parser can ever be substituted underneath
such a caller, however carefully the prefixes were factored out. Only the answer can move; the
question has to stay.

So `find_references` does not learn that X# sends with `:`. It asks what the appearance of an
identifier looks like, and an X# file answers `MemberAccess` where a C# file would answer something
else. A tree-sitter or Roslyn analyser answers the same questions from a syntax tree, and the
callers — reference classification, `find_definition`, the import extractor, the file filter — see
no difference.

The last question is the one #57 did not name. The ticket lists four and expects reference
classification to be a caller of the lexical one, but a lexical state cannot say that `:=` is an
assignment in X# and a syntax error in C#, and the acceptance criteria require exactly that. The
resolution is to make "what does this appearance look like" a question of the same kind rather than
to let the caller reach for the operators: it is answerable from a profile today and answerable
better from a parse tree tomorrow, which is the test every question here has to pass.

## The two extension points, which are different

**A new language is a new `LanguageProfile`** in the table in `Languages`: comment openers and
block-comment pairs, string delimiters and their escape convention, assignment and member-access
operators, import line forms, declaration keywords, whether the language separates declaration from
implementation, and generated-file patterns. Adding one is one entry and touches no caller and no
`switch`, and the table is the whole registration, so a reader sees every supported language at
once.

**A better implementation for a language already covered is a different `ILanguageAnalyzer`**, laid
over the same extensions in the registry. The intended first use is C# through Roslyn and the
C family through tree-sitter. X# has no grammar to be had, which is exactly why the text profile
stays a first-class implementation and not a stopgap.

Because the two coexist, `Evidence` is on every answer rather than on the build. A reply that called
itself textual after a parser was registered for the language it answered about would be
understating what it knows, and one that did the reverse would be overstating it.

## Where this leaves "no language analysis"

`CONTEXT.md` says a reference is determined from the text alone, never from language analysis. That
line stays where it was for the answers this build gives: a table of punctuation per extension is
still text, and `TextAnalyzer` has no compiler, no symbol table and no parse tree. A reference it
finds is still strong evidence and never proof, and every reply still says so.

What changes is that the line is now drawn at the seam rather than at the horizon. A parser-backed
analyser is the anticipated end of this and not a violation of it; when one is registered, the rule
becomes "a reference is determined from the text alone unless the reply says otherwise", which is
what `Evidence` is for.

The rule that keeps the profiles from sprawling is the one the declaration modifiers already follow:
a form a profile does not know costs an unplaced reference, a form it invents costs a wrong answer
reported as a right one. A doubtful form is left out.

## Consequences

- **An extension no profile covers falls back to a profile that was today's behaviour exactly.** A
  project written in a language nobody declared must read no worse after this than before it, and
  the way to be sure is for the fallback to be the old rules unchanged — including the rules that
  are wrong somewhere, such as a leading `#` opening a comment. The file-level scan (#53) is the one
  thing it gained after the fact, because "a `/* */` stays open until it closes" is not a rule any
  language it could be covering disagrees with, and leaving it out would have kept the failure the
  scan exists to fix on exactly the projects nobody declared a profile for.
- **`find_references` reads X#, Delphi and PL/SQL writes.** `:=` is an assignment where it is one,
  `=` stays a comparison where it is one, and the WRITES section stops being empty on a third of
  the codebases here.
- **The declaration prefilter is per language, and is a question rather than a pattern.** An
  analyser says which lines it needs — all of them, none of them, or the ones an RE2 pattern matches
  — and `ReferenceSearch` turns that into a query. A bare pattern would have made every
  parser-backed analyser invent a regex it has no use for, which is the mechanism leaking through
  the seam that hides it. A reference search issues one scope query per language among the files it
  read, and none at all for a language that declares nothing it can read.
- **`Language/` is a module and a leaf.** It owns the extension-to-language table that
  `Infrastructure/Languages.cs` used to hold, because the language a file is written in and the way
  it opens a comment are one fact and were two copies of one. It references no other module, which
  is what `ModuleBoundaryTests` now asserts.
- **`IsGenerated` and `ImportOn` have no caller yet.** They are questions the seam has to answer for
  the import-edge and file-filter tickets to be callers of it rather than authors of a second copy;
  they are tested directly until then.
- **A modifier list says what introduces a name, and a second, narrower one says what opens a
  scope.** One list could not answer both (#83). It was read twice — by the analyser to find
  declarations, and transitively by `DeclarationScope` to decide which declaration a reference sits
  inside — and `local`, `instance`, `define`, `var` and `const` are a yes to the first question and a
  no to the second. With one list the only way to keep the second answer right was to leave them out
  of the first, so every reference below a `local cLabel := …` was labelled with the method rather
  than with the local, at the price of X# files that are nothing but `define` lines reporting that
  they declare nothing at all. The C family never paid that price and got the wrong answer instead:
  its list holds `const`, `readonly`, `val` and `let`, so a *local* `const int Max = 10;` matched the
  member shape and every reference indented under it was labelled `Max`. The scope list is a subset
  of the name list, which stops a profile naming a scope-opener no declaration shape matches, and a
  profile that names no scope list keeps today's behaviour — the fail-safe direction, since
  forgetting one costs the old label and not a language whose references quietly stop being placed.
  The distinction rides on the answer, decided by the match that produced the name: `Declared` says
  whether the line opens a scope, and `DeclarationScope` asks rather than reading a list, which is
  the rule this whole seam is built on. For the same reason as the lists themselves, SQL's
  `or replace` is one entry and not a bare `or`, which would have made the continuation line of every
  `WHERE` clause read as a declaration.
- **Keyword matching follows the profile's own case rule everywhere, not only in its patterns.**
  SQL, PL/SQL, X# and Delphi are case-insensitive and shout their keywords; an ordinal check beside
  a case-insensitive pattern made one line a declaration for the scope label and a call for the
  counts.
- **The lexical scan is one vectorised pass and allocates almost nothing.** The lines this reads are
  whatever the index holds, and a minified bundle within `Index:MaxFileBytes` is a single line of
  millions of characters. Copying the prefix per match and re-walking it per delimiter turned one
  `find_references` into half a minute inside one tool call; it now jumps between the characters
  that can begin a comment or a literal and skips the code between them.
- **The declaration/implementation split is decided off the file-level position (#54).** The section
  is a field on the analyser's own record beside the open comments and literals, moved by a phrase
  the profile names — Delphi's `interface` and `implementation`, PL/SQL's `create package` and
  `create package body` — and it holds from the line the phrase stands on, because the header of a
  package body is itself the first declaration in the body. That is also what lets a spec and a body
  in two different files be told apart without either knowing the other exists, which the extension
  could not have done: the same two headers appear inside one `.sql` script. A caller that has not
  walked the file to the line — `find_references` asking only what a line declares, for its scope
  label — passes `FilePosition.Unknown` and is answered with a null role. Null and not
  `Declaration`, because either name would be a guess, and the announcement is what an agent wanted
  least and what sorts first.
- **A declaration head is a shape per language and not one shape.** The C family writes the return
  type before the name, the xBase and SQL families after it, Delphi writes its type as
  `TCustomer = class(TBase)` and its implementation head as `procedure TCustomer.Save;`, and the SQL
  family opens a body with a word — `as`, `is` — where the others open one with a bracket. Each is a
  shape a profile turns on and each word is a word the profile lists, for the reason `new` is: a
  keyword welded into the shared pattern is a language fact the profile author cannot see, and this
  module has absorbed that one twice already. The combined pattern is what `DeclarationCandidates`
  publishes, so a shape added for one language costs the others nothing and the engine narrows every
  file with its own language's.
- **The per-language narrowing is built from the extensions the project holds, not from the
  language table.** `find_definition` needs one query covering every language at once, and the
  obvious way to write it — walk the registrations, emit a branch per language, and a `NOT IN` for
  the remainder — would have put the extension-to-language table into `Search/` as SQL. Asking the
  index for its own distinct extensions and resolving each through `LanguageRegistry.For` gives the
  same query, narrower (the nine extensions in the project, not the forty that could be) and with no
  remainder to name: an extension no profile covers resolves to the fallback like any other, and the
  table stays on its own side of the seam.
- **A comment or a literal stays open across lines, and a bounded scan says where it stopped (#53).**
  Classifying a line on its own reported the second line of a commented-out block as a call, which is
  a deleted call under the heading an agent trusts most. The position is built by walking a file's
  lines from the first, because no line further down can be known to be outside everything; a
  search therefore reads the lines above each line it cares about once per file, to
  `FilePositions.MaxScanLines`, and everything past that bound is unplaced rather than guessed. That
  walk is one component and not one per search, because a second copy could read one file two ways.
  Which literals span lines and where their interpolation holes are comes from the profile, so
  C#'s verbatim, raw and interpolated forms and the template literal are read as what they are, and
  a language with no multi-line literal — SQL's quoted string, which could legally hold a newline —
  is deliberately not given one: an invented spanning form costs every line below it.
- **A keyword is a keyword, not punctuation.** `new` comes from the profile like every other operator
  and is matched on a word boundary under the profile's own case rule, so `renew(` constructs
  nothing and a language that builds an object another way — X#'s `Foo{…}` — simply lists none.
  One C-family keyword welded into the shared path was the last thing the seam had not absorbed.

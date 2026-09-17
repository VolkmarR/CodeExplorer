# Language facts are resolved per file, behind a seam a real parser can implement

Everything this server knows about a programming language is resolved from a file's extension and
answered by one component, `ILanguageAnalyzer`, in the `Language/` module. Callers ask it questions
and never read its data.

The questions are:

- What is the lexical state at this position — code, comment, or string?
- What, if anything, does this line declare, and is it a declaration or an implementation?
- What does this file import?
- Is this file generated?
- What does every appearance of an identifier on this line look like — a write, a call, a read, a
  type use?

Every answer is an `Answer<T>`, which carries the value and the `Evidence` it was reached by:
`Text` for one read from the source, `Parsed` for one a real parser produced. It is on the answer
and not on the analyser, because an analyser that parses what it can and falls back on the rest
tells the truth only per answer.

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
else. A tree-sitter or Roslyn analyser answers the same five questions from a syntax tree, and the
callers — reference classification, `find_definition`, the import extractor, the file filter — see
no difference.

The fifth question is the one #57 did not name. The ticket lists four and expects reference
classification to be a caller of the first, but a lexical state cannot say that `:=` is an
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

- **An extension no profile covers falls back to a profile that is today's behaviour exactly.** A
  project written in a language nobody declared must read no worse after this than before it, and
  the way to be sure is for the fallback to be the old rules unchanged — including the rules that
  are wrong somewhere, such as a leading `#` opening a comment.
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
- **A modifier list is also a scope list, so it holds only what opens a scope.** `local`, `instance`,
  `var` and `const` introduce a name and not a scope; with them in, every reference below a
  `local cLabel := …` was labelled with the local rather than with the method it sits in. For the
  same reason SQL's `or replace` is one entry and not a bare `or`, which would have made the
  continuation line of every `WHERE` clause read as a declaration.
- **Keyword matching follows the profile's own case rule everywhere, not only in its patterns.**
  SQL, PL/SQL, X# and Delphi are case-insensitive and shout their keywords; an ordinal check beside
  a case-insensitive pattern made one line a declaration for the scope label and a call for the
  counts.
- **The lexical scan is one vectorised pass and allocates nothing.** The lines this reads are
  whatever the index holds, and a minified bundle within `Index:MaxFileBytes` is a single line of
  millions of characters. Copying the prefix per match and re-walking it per delimiter turned one
  `find_references` into half a minute inside one tool call; it now jumps between the characters
  that can begin a comment or a literal and skips the code between them.
- **The declaration/implementation split is declared but not yet decided.** A profile says whether
  its language has one; telling a Delphi `interface` section from its `implementation` needs the
  file-level position #53 builds, and until then a declaration's role is null. Null and not
  `Declaration`, because either name would be a guess and the null-rather-than-guess rule is the one
  this module is written around.
- **A keyword is a keyword, not punctuation.** `new` comes from the profile like every other operator
  and is matched on a word boundary under the profile's own case rule, so `renew(` constructs
  nothing and a language that builds an object another way — X#'s `Foo{…}` — simply lists none.
  One C-family keyword welded into the shared path was the last thing the seam had not absorbed.

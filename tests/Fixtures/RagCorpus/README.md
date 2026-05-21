# RAG eval corpus

Hand-picked public-domain fixtures for the Stage 1 retrieval eval harness.
Used by `tests/Retrieval/RetrievalEvalHarness.cs` to compute `recall@5` /
`recall@20` against the golden Q/A set in `retrieval_golden.json`.

## Fixtures

| File | Source | License basis | SHA-256 |
|------|--------|---------------|---------|
| `faa_ac_00-46f.pdf` | [FAA Advisory Circular 00-46F — Aviation Safety Reporting Program](https://www.faa.gov/documentLibrary/media/Advisory_Circular/AC_00-46F.pdf) | US federal government work — public domain under 17 USC §105 | `77FF8999046B99E764E46AB8947CC2A4482C6912FA1FBA3618C4106D2D849039` |
| `faa_ac_90-66c.pdf` | [FAA Advisory Circular 90-66C — Non-Towered Airport Flight Operations](https://www.faa.gov/documentLibrary/media/Advisory_Circular/AC_90-66C.pdf) | US federal government work — public domain under 17 USC §105 | `3CED94D15F8E79835A848A9DB3FFE3341B635F96BFB7D3375D6412B4B4A113E0` |
| `ntsb_special_investigation.pdf` | [NTSB SIR-18/01 — End-of-Track Collisions at Terminal Stations](https://www.ntsb.gov/investigations/AccidentReports/Reports/SIR1801.pdf) | US federal government work — public domain under 17 USC §105 | `FDF62D8F70612430983EE1A2EE1BF48CB2701E96DC9CEBEDAB9C4DA9EE4FA5C5` |
| `markdown_sample.md` | Authored in-repo for this corpus | No third-party content; same license as the surrounding repo | (text, no SHA pinned) |

All three PDFs are works of the US federal government and are therefore
public domain under 17 USC §105 (no copyright in works prepared by federal
employees as part of their official duties). The markdown file is original
content authored for this fixture corpus.

## Topic mix

Aviation + railroad accident investigation + RAG concepts. The mix is
intentional: different writing styles, different document structures
(short ACs, long NTSB report with TOC and appendices, conversational
markdown), and different terminology footprints. A retriever that
over-fits one domain will show up in the per-question hit table.

## Updating fixtures

If a fixture is replaced (e.g., a new AC revision), update:
1. The PDF in this directory (overwrite).
2. The SHA-256 column in the table above.
3. Any affected entries in `retrieval_golden.json`.
4. Delete `tests/Retrieval/baseline.json` so the next eval run writes a
   fresh baseline — the prior baseline is meaningless against the new
   content.

Always verify the source URL and license basis before adding a new
fixture. Public-domain-by-17-USC-§105 is the rule, no exceptions. If a
candidate file is not a US federal government work, do not commit it —
the local-corpus extension (`FREEAI_TEST_LOCAL_CORPUS_PATH`) exists for
private third-party material.

## Golden Q/A format

`retrieval_golden.json` is a JSON array. Each entry:

```json
{
  "id": "ac0046f-01",
  "fixture": "faa_ac_00-46f.pdf",
  "question": "What is the purpose of the Aviation Safety Reporting Program?",
  "correct_pages": [1]
}
```

- `id` — stable identifier; do not rename without bumping the baseline.
- `fixture` — file in this directory (relative path).
- `question` — natural-language query the harness embeds and searches.
- `correct_pages` — 1-indexed PDF page numbers (per PdfPig) that contain
  the answer. A retrieved chunk counts as a hit if its
  `(source_file_name, page)` matches. Empty `correct_pages` (used for
  markdown and other non-paginated files) means filename-only match.

## Local corpus extension

For real-workload evaluation (e.g., Chuck's Guides for DCS, internal
manuals, copyrighted reference material), point the env var
`FREEAI_TEST_LOCAL_CORPUS_PATH` at a directory on your machine
containing:

- The PDF/markdown/text files
- A `local_golden.json` in the same shape as `retrieval_golden.json`

The harness will write a `local_baseline.json` next to your fixtures on
first run, then assert against it on subsequent runs. Nothing under
`FREEAI_TEST_LOCAL_CORPUS_PATH` is committed to the repo.

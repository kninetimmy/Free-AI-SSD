# Retrieval-Augmented Generation Primer

A short overview of the components, trade-offs, and failure modes of a
retrieval-augmented generation (RAG) pipeline. Used as a fixture for the
Free-AI-SSD RAG eval harness.

## Pipeline stages

A RAG pipeline has four stages: ingest, chunk, embed, and retrieve. Each
stage has its own failure modes and tuning knobs.

### Ingest

The ingest stage parses source documents into plain text. PDFs are parsed
via PdfPig; plain text formats (txt, md, csv, json) are read directly.
A magic-byte check rejects files whose extension does not match their
actual content, which is the first line of defense against malicious
uploads. Files larger than a configurable limit are skipped with a
warning.

### Chunk

The chunk stage splits parsed text into overlapping windows. Each chunk
is large enough to carry meaningful context but small enough to embed
without losing precision. Chunk overlap helps preserve continuity across
boundaries — a sentence cut in half between two chunks is recoverable
because both chunks see part of it.

### Embed

The embed stage converts each chunk into a dense vector via an embedding
model. The current default is `nomic-embed-text`, which produces 768
dimensions per chunk. Embeddings are L2-normalized at write time so that
dot product equals cosine similarity at query time, which lets the
retrieval step use SIMD dot product instead of a more expensive cosine.

### Retrieve

The retrieve stage embeds the query, scores every chunk in the library
by similarity, and returns the top K matches. Free-AI-SSD uses a brute
force linear scan over up to ~10,000 chunks per library; approximate
nearest neighbor indexes were rejected because they add native
dependencies that conflict with the portable single-executable deployment
model.

## Failure modes

The two most common failure modes in retrieval are recall regression and
grounding failure. Recall regression means the right chunk exists in the
index but does not score high enough to appear in the top K. Grounding
failure means the chunk does appear in the top K but the model fails to
cite it or hallucinates around it. The first is a retrieval problem; the
second is a prompting problem. The eval harness measures the first
directly via recall@K.

## Hybrid retrieval

Pure dense retrieval struggles with exact-match facts that appear in
repetitive sections, appendices, captions, and tables. The classic fix
is to fuse dense scores with lexical scores from a BM25 or FTS index
using reciprocal rank fusion. Free-AI-SSD's vector store uses SQLite,
which ships with FTS5, so adding a lexical lane requires no new
dependency.

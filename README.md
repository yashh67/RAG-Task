# ChatBot

A small ChatGPT-style chatbot that runs **Qwen3 locally on the CPU** with
[LLamaSharp](https://github.com/SciSharp/LLamaSharp), in an ASP.NET Core Blazor app (.NET 8,
Interactive Server). It has two modes, switched at the top of every chat:

- **Chatbot**: a normal multi-turn assistant.
- **Documents**: answers questions **only from your documents** with page citations like
  `[U.S. Government Publishing Office 2018 Annual Report, p. 12]` and a Sources list under each
  answer. Three documents are built in and ready at startup (see below), and you can upload more.
  Search is hybrid: vector search with Qwen3-Embedding plus BM25 keyword search.

No GPU, no database, no Docker, no accounts. Chats live in memory; documents live in `docs/` and
their search index in `data/index/`.

## Prerequisites

- **.NET 8 runtime** and an SDK that can build `net8.0` (the .NET 8 SDK or newer). This repo's
  `global.json` pins SDK 10.0.401 with `latestFeature` roll-forward. If you only have the .NET 8
  SDK, delete `global.json` or change the version.
- A 64-bit CPU with AVX2 (LLamaSharp picks the best CPU backend automatically).
- **RAM**: both models are loaded fully into system RAM.

| What | File | Download size | RAM used (approx.) |
|---|---|---|---|
| Chat model **Qwen3-4B Q4_K_M** (default) | `models/Qwen3-4B-Q4_K_M.gguf` | 2.5 GB | ~4 GB with an 8K context |
| Chat model Qwen3-1.7B Q4_K_M (smaller) | `models/Qwen3-1.7B-Q4_K_M.gguf` | 1.1 GB | ~2.5 GB |
| Embedding model **Qwen3-Embedding-0.6B Q8_0** | `models/Qwen3-Embedding-0.6B-Q8_0.gguf` | 0.64 GB | ~0.8 GB |
| App, documents and index | | | ~0.3 GB + a few MB per document |

**Total: about 5 GB free RAM with the defaults** (16 GB machine recommended), or about 3.5 GB with
Qwen3-1.7B (8 GB machine).

## 1. Download the models

Put the files in `models/` at the repo root (`*.gguf` is git-ignored). File names were checked on
Hugging Face.

```powershell
# Chat model (default), official Qwen repo
curl -L -o models/Qwen3-4B-Q4_K_M.gguf https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf

# Embedding model for document search, official Qwen repo (only Q8_0 and f16 exist there)
curl -L -o models/Qwen3-Embedding-0.6B-Q8_0.gguf https://huggingface.co/Qwen/Qwen3-Embedding-0.6B-GGUF/resolve/main/Qwen3-Embedding-0.6B-Q8_0.gguf

# Optional: smaller chat model for 8 GB machines (Q4_K_M is from unsloth; the official repo has only Q8_0)
curl -L -o models/Qwen3-1.7B-Q4_K_M.gguf https://huggingface.co/unsloth/Qwen3-1.7B-GGUF/resolve/main/Qwen3-1.7B-Q4_K_M.gguf
```

With the Hugging Face CLI instead:
`huggingface-cli download Qwen/Qwen3-Embedding-0.6B-GGUF Qwen3-Embedding-0.6B-Q8_0.gguf --local-dir models`.

If a model file is missing, the app still starts, logs the download command and shows it on the page.
Without the embedding model, Chatbot mode works but Documents mode is disabled.

## 2. Build the document index (first time only)

Everything in `docs/` is indexed automatically. Three **built-in documents** ship with the app.
They are listed in `Rag:BuiltInDocuments`, show a *Built-in* badge, and can't be deleted from the
app, so they are always there when the app starts:

| File | Title shown | Structure | First indexing* |
|---|---|---|---|
| `docs/iMac_PER_Oct2024.pdf` | iMac Product Environmental Report | 12-page designed brochure: callouts, footnotes, a carbon-footprint table; 47 chunks | 48 s |
| `docs/GPO-Anual-report.pdf` | U.S. Government Publishing Office 2018 Annual Report | 82 pages: two-column narrative + audited financial statements; 181 chunks | ~8 min |
| `docs/GEP-Jun-2026.pdf` | Global Economic Prospects -- June 2026 | 200-page World Bank report: chapters, boxes, charts, wide country tables; 607 chunks | ~50 min |

\* On an i5-1235U laptop CPU. The result is cached in `data/index/{sha256}.json`, and every later
start loads it in about a second, so **all three documents are Ready as soon as the app starts**.
Documents are indexed smallest first, so the short ones are usable while the GEP is still running.
Indexing saves a checkpoint (`{sha256}.partial.json`) every 25 chunks, so an interrupted run
continues where it stopped. If a built-in file goes missing from `docs/`, the startup log warns
about it; copy it back to restore it.

Build the cache ahead of time, without starting the web app:

```powershell
dotnet run --project ChatBot -- --index-only
```

```
Ready    GEP-Jun-2026.pdf: 200 pages, 607 chunks
Ready    iMac_PER_Oct2024.pdf: 12 pages, 47 chunks
Ready    GPO-Anual-report.pdf: 82 pages, 181 chunks
```

The caches are already built for these three files (`data/index/`, about 12 MB in total), so you
only need this step after changing a document, the embedding model or the chunk settings.

If you skip this step the app indexes in the background on first start. The sidebar shows the
progress, and each document can be searched as soon as it is Ready.

## 3. Run

```powershell
dotnet build
dotnet run --project ChatBot
```

Open http://localhost:5097.

> **Stop the app (Ctrl+C) before running `dotnet build` again.** While it runs, Windows locks
> `ChatBot.exe` and the build fails with *"Could not copy apphost.exe … The file is locked by:
> ChatBot (PID)"*.

## Using the app

### Chatbot or Documents

Use the **Chatbot | Documents** switch at the top of the chat. Every chat remembers its own
mode, a new chat starts in the mode you are using, and the sidebar shows a chat or document icon
next to each chat.

| | Chatbot | Documents |
|---|---|---|
| Answers from | the model's own knowledge | only excerpts from your documents |
| Citations / Sources | no | yes, `[Document, p. X]` plus a collapsible Sources list |
| Sidebar | chats | chats + Documents panel |
| Header | | **Search in** (All documents or one document), index status, **Upload document** |

In Documents mode the model says *"I couldn't find that in the documents."* instead of guessing.
Sending is disabled until at least one selected document is **Ready**.

While a reply is on its way, a status line shows *Waiting for the model…* (another chat is using
it), *Searching documents…*, *Thinking…* (with a seconds counter), then *Typing…*. The **Stop**
button keeps the partial answer.

### Uploading documents

Click **Upload document** in the header or **Upload** in the sidebar's Documents panel.

| Type | Read with | Location in citations | Headings detected from |
|---|---|---|---|
| **PDF** (.pdf) | UglyToad.PdfPig, page by page | printed page numbers: `p. 12`, `pp. 12–13` | larger or bold font, numbering |
| **Word** (.docx) | DocumentFormat.OpenXml | paragraph numbers: `¶ 4–6` | Word heading styles / outline levels |
| **Text** (.txt) | | paragraph numbers | ALL CAPS, numbered or underlined lines |
| **Markdown** (.md) | | paragraph numbers | `#` headings |

- Max **20 MB** per file; several files can be selected at once.
- Uploads are saved to `docs/` and indexed in the background, one at a time, without re-indexing
  other documents. The panel shows **Indexing NN%** → **Ready** or **Failed**, with page and chunk counts.
- **Duplicates** (same SHA-256 content) and **unsupported or invalid files** are rejected with a message.
- **Scanned PDFs**: pages with images but no text are reported with a warning (there is no OCR).
  A PDF with no text at all is marked Failed.
- **Delete** (× in the panel, with confirmation) removes the file from `docs/`, its cache file and
  its chunks from the index. Built-in documents have no delete button.
- Documents are **shared by all users and sessions**, and they survive restarts (chats don't).

## Configuration (`ChatBot/appsettings.json`)

### Chat model (`Llm`)

| Key | Default | Meaning |
|---|---|---|
| `ModelPath` | `models/Qwen3-4B-Q4_K_M.gguf` | GGUF file. Relative paths are looked up from the project folder and its parents. |
| `ContextSize` | `8192` | Context window (prompt + reply) in tokens. Bigger uses more RAM. |
| `Threads` | `0` | CPU threads; `0` = physical cores. Also used by the embedding model. |
| `MaxResponseTokens` | `1024` | Maximum reply length. |
| `Temperature` / `TopP` / `TopK` | `0.7` / `0.8` / `20` | Qwen's recommended non-thinking sampling. |
| `EnableThinking` | `false` | Qwen3 thinking mode. `<think>` text is always hidden and never stored. |

### Documents (`Rag`)

| Key | Default | Meaning |
|---|---|---|
| `EmbeddingModelPath` | `models/Qwen3-Embedding-0.6B-Q8_0.gguf` | Embedding GGUF. |
| `QueryPrefix` | `Instruct: Given a question, retrieve passages…\nQuery:` | Qwen3-Embedding's query instruction. Documents get `DocumentPrefix` (empty). For **nomic-embed-text-v1.5** use `search_query: ` and `search_document: `. |
| `AppendEndOfText` | `true` | Add `<|endoftext|>` when the tokenizer doesn't already (Qwen3-Embedding's tokenizer does). |
| `DocsFolder` / `IndexFolder` | `docs` / `data/index` | Where documents and cache files live. |
| `BuiltInDocuments` | `["GPO-Anual-report.pdf"]` | File names in `docs/` that ship with the app: always indexed at startup and can't be deleted from the UI. |
| `MaxUploadMB` | `20` | Upload size limit per file. |
| `ChunkTokens` / `ChunkOverlapTokens` | `400` / `60` | Chunk size and overlap. Changing them re-indexes on the next start. |
| `VectorTopK` / `KeywordTopK` / `FinalTopK` | `10` / `10` / `5` | Candidates from each search, and how many survive Reciprocal Rank Fusion. |
| `ContextTokens` | `2500` | Token budget for excerpts in the prompt. |
| `RewriteTurns` / `RewriteTemperature` | `3` / `0.1` | Follow-up rewriting: how many earlier turns it sees, and its temperature. |

Any key can be overridden on the command line, for example
`dotnet run --project ChatBot -- --Llm:ModelPath=models/Qwen3-1.7B-Q4_K_M.gguf`.

## How it works

```
docs/*.pdf|docx|txt|md ─► DocumentParser ─► TextChunker ─► EmbeddingService ─► data/index/{hash}.json
                                                                                     │ (loaded at startup)
question ─► rewrite follow-up (chat model) ─► vector top 10 + BM25 top 10 ─► RRF top 5 ─► fit 2,500 tokens
        ─► prompt: system + trimmed history + "CONTEXT: [1] (Doc, Section, p. X–Y) … QUESTION: …" ─► Qwen3-4B ─► answer + Sources
```

- **`DocumentParser`**: rebuilds PDF lines from letter positions. The GPO report has no space
  characters, so spaces come from letter gaps. It also:
  - removes headers, footers and page numbers that repeat on several pages, and footnote markers
    (small raised digits such as `content.¹²`)
  - joins words hyphenated across lines, and expands ligatures (`ﬁ` → `fi`)
  - drops chart axis labels
  - writes table rows on one line as `cell | cell`
  - reorders text that two columns write line by line
  - detects headings by font and numbering, including bold run-in headings, while ignoring
    number callouts (`346 kg`) and large-print intro sentences
  - reads printed page numbers from the headers and footers and checks them against neighbouring pages
- **`TextChunker`**: splits by section, then paragraph, then sentence, into chunks of about 400 tokens
  with about 60 tokens of overlap, never cutting a sentence. Each chunk keeps its document, section,
  pages and text; the embedded text is `"{section}\n{text}"`.
- **`EmbeddingService`**: Qwen3-Embedding-0.6B via `LLamaEmbedder` (last-token pooling, query
  instruction), with L2-normalized vectors and its own lock.
- **`DocumentLibrary`**: the shared document list, background indexing queue, uploads, deletes and
  the cache. A cache file is reused only when the file hash and the settings (embedding model,
  chunk sizes, parser version) match.
- **`Retriever`**: cosine similarity + in-memory BM25, merged with Reciprocal Rank Fusion (k = 60).
- **`ChatService`**: in Documents mode, rewrites follow-ups into a standalone question using the
  last 3 turns, searches the selected documents, and builds the prompt. History holds only the
  plain question and answer; excerpts are never stored. Old history is trimmed before excerpts.
  Small models sometimes cite by excerpt number (`[1, p. 33]`), so `CitationFormatter` rewrites
  those into `[Document, p. 33]` using the excerpt they refer to.
- **`LlmService`**: one model instance with `StatelessExecutor`. Requests run one at a time behind a
  `SemaphoreSlim(1,1)`, and replies stream token by token.
- **`SessionStore`**: chats in memory, per anonymous browser cookie, removed after 2 hours idle.

## Checking it

- **Document questions**: [`eval/questions.md`](eval/questions.md) has 30 questions written from the
  three built-in documents: facts, table lookups, follow-ups, questions whose answer isn't in the
  documents, cross-document questions, and upload and document-selector tests. Each has the
  expected answer and the accepted printed pages.
- **Chatbot mode follow-ups**: "What is RAG?" → "What are its main components?" → "Can you explain
  the second one in detail?" should explain the 2nd item. In a new chat, "Can you explain the
  second one?" should get a clarifying question.

## Known limitations

- **Speed (CPU only).** On an i5-1235U laptop the chat model reads prompts at about 25 tokens/s and
  writes about 7–10 tokens/s. A Documents answer sends up to about 3,000 tokens, so **the first
  word takes about 40–90 seconds**, plus 10–20 s to rewrite follow-ups. To speed it up, lower
  `ContextTokens` (e.g. 1500) or `FinalTopK` (e.g. 3), or use Qwen3-1.7B. Embedding runs at about
  90 tokens/s, which is why first-time indexing takes minutes (50 min for the 200-page GEP).
  Uploads are indexed one at a time, so a new upload waits for any document still indexing.
- **One model, one request at a time.** Other users see "Waiting for the model…".
- **Scanned pages**: no OCR. Image-only pages are skipped with a warning.
- **Complex tables**: rows are kept as `cell | cell` lines, which works for simple statements.
  Merged cells, multi-line cells, tables placed side by side and charts can come out jumbled or
  lose the link between a number and its column heading.
- **Charts and figures**: only their text survives (titles, panel labels, notes). Bars and lines are
  images, so values that appear only in a chart can't be found.
- **Page numbers**: citations use printed page numbers. The GPO PDF holds two documents with
  separate numbering (the report p. 1–28, the financial statements p. 1–51), so a page number can
  appear twice; the Sources list shows the section, which tells them apart. Pages without a printed
  number (covers, and the GEP's front matter, which uses roman numerals) are cited as `PDF p. N`.
- **Hyphenated compounds split across lines** lose their hyphen ("conflict-\nrelated" →
  "conflictrelated"), because a line-break hyphen can't be told apart from a real one.
- **Heading detection is heuristic**: a few headings are missed or merged, which only affects the
  section label, not the text.
- **DOCX/TXT/MD have no pages**: they are cited by paragraph number (`¶`).
- **Documents are shared and unauthenticated**: anyone who can open the app can upload or delete
  them. Chats are private per browser cookie but are lost on restart.
- The full prompt is processed again on every message (no KV-cache reuse).
- A message longer than the context window is rejected.

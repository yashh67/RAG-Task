# RAG evaluation questions

Questions written from the three built-in documents in `docs/`:

| File | Title the app shows | Structure |
|---|---|---|
| `GPO-Anual-report.pdf` | U.S. Government Publishing Office 2018 Annual Report | 82 pages: two-column narrative + audited financial statements with tables (groups A–E) |
| `GEP-Jun-2026.pdf` | Global Economic Prospects -- June 2026 | 200 pages: World Bank report with chapters, boxes, charts and large country tables (group F) |
| `iMac_PER_Oct2024.pdf` | iMac Product Environmental Report | 12 pages: designed brochure with callouts, footnotes and a carbon-footprint table (group G) |

Pages are the **printed** page numbers the app cites. For the GPO report: The PDF holds two documents with
separate numbering: the narrative report (printed p. 1–28 = PDF p. 3–30) and the audited financial
statements (printed p. 1–51 = PDF p. 32–82). So "p. 9" can exist twice; the Sources list shows the
section, which tells them apart.

Run them in the app with **Answer from documents** on and **All documents** selected. Questions in
the same group are asked in one chat, in order (follow-ups depend on the previous answer). Start a
new chat for each group.

A question passes when the answer contains the expected fact (numbers exactly) and cites a page
listed under "Accepted pages". Several facts appear on more than one page, so any listed page counts.

## Group A: facts from the narrative

| # | Question | Type | Expected answer | Accepted pages |
|---|---|---|---|---|
| A1 | When did GPO first open for business? | Date | March 4, 1861 (the day Abraham Lincoln was inaugurated) | 6 |
| A2 | How many depository libraries take part in the FDLP? | Number | 1,133 | 6, 13, 17 |
| A3 | How many employees did GPO have at the end of FY 2018? | Number (table) | 1,737 | 10, 12, 13 |
| A4 | How many passports did GPO produce in FY 2018? | Number, two forms | 15,901,600 ("approximately 16 million" in the Director's message) | 4, 12, 13 |

## Group B: follow-ups (one chat)

| # | Question | Type | Expected answer | Accepted pages |
|---|---|---|---|---|
| B1 | How many Preservation Steward agreements has GPO signed? | Number | 36 | 18 |
| B2 | How many of those were signed in FY 2018? | Follow-up ("those") | 20 | 18 |
| B3 | What was GPO's fund balance with Treasury at the end of FY 2018? | Number | $644.8 million ($644,807 thousand) | 24, 25, 9, 27, 29, 38 |
| B4 | And at the end of FY 2017? | Follow-up (elliptical) | $585.9 million ($585,883 thousand) | 24, 9, 27, 29, 38 |

## Group C: financial statements and tables

| # | Question | Type | Expected answer | Accepted pages |
|---|---|---|---|---|
| C1 | What were GPO's total operating revenues in FY 2018? | Number (statement) | $874,466 thousand / $874.5 million | 12, 23, 28 |
| C2 | What was GPO's net income in FY 2018? | Number, two figures | $52.6 million ($52,624 thousand); $20.4 million after the capital projects reserve and workers' compensation adjustment | 4, 23, 24, 28, 13 |
| C3 | What is the estimated useful life of computer software? | Table lookup | 3 years | 33 |
| C4 | Which firm audited GPO's FY 2018 financial statements, and what opinion did it give? | Names | KPMG LLP; an unmodified (clean) opinion | 4, 24, 23 |

## Group D: not in the documents

| # | Question | Type | Expected answer |
|---|---|---|---|
| D1 | What was GPO's revenue in FY 2022? | Outside the report's years | "I couldn't find that in the documents." |
| D2 | Who is the CEO of Microsoft? | Unrelated | "I couldn't find that in the documents." |

## Group E: uploads and the document selector

Upload `eval/sample-cafeteria-policy.md` first (it is a made-up test document, not real data).

| # | Setup | Question | Expected answer |
|---|---|---|---|
| E1 | Search in: All documents | What time does the staff cafeteria open on weekdays? | 7:30 a.m., cited as the sample policy with a ¶ paragraph number |
| E2 | Search in: the GPO report only | What time does the staff cafeteria open on weekdays? | "I couldn't find that in the documents." |
| E3 | Upload the same file again | (none) | Rejected as a duplicate |
| E4 | Delete the sample document | Ask E1 again with All documents | "I couldn't find that in the documents." |

## Group F: Global Economic Prospects, June 2026 (one chat for F3–F4)

The GEP's front matter (cover, foreword, executive summary) has no printed page numbers, so it is
cited as `PDF p. N`. Chapter pages use the printed numbers (PDF p. 23 = p. 1).

| # | Question | Type | Expected answer | Accepted pages |
|---|---|---|---|---|
| F1 | What is the World Bank's forecast for global growth in 2026? | Number | 2.5 percent (down from 2.9 percent in 2025) | 3, 5, 30, 178, PDF 19 |
| F2 | How fast are emerging market and developing economies expected to grow in 2026? | Number | 3.6 percent | 20, PDF 19 |
| F3 | By how much are commodity prices expected to rise in 2026? | Number | About 22 percent | 3, 15 |
| F4 | What had been expected in January? | Follow-up (elliptical) | A 7 percent decline | 3 |
| F5 | What is the growth forecast for Egypt for fiscal year 2026/27? | Table lookup (wide country table) | 4.0 percent | 73 |

## Group G: iMac Product Environmental Report (one chat for G3–G4)

| # | Question | Type | Expected answer | Accepted pages |
|---|---|---|---|---|
| G1 | How much less energy does iMac use than the ENERGY STAR requirement? | Number | 58 percent less | 7 |
| G2 | What is the total carbon footprint of iMac? | Number (callout + table) | 346 kg CO2e | 2, 10 |
| G3 | How much recycled content is in the iMac packaging? | Number | 76 percent | 6 |
| G4 | Does it contain any plastic? | Follow-up ("it") | 100 percent fiber-based, no plastic except inks, coatings and adhesives | 6 |

## Group H: several documents at once

| # | Setup | Question | Expected answer |
|---|---|---|---|
| H1 | Search in: All documents | What percentage of recycled content does iMac contain? | More than 15 percent, cited from the iMac report (p. 1, 2 or 4), not from the other documents |
| H2 | Search in: the GPO report only | How much less energy does iMac use than the ENERGY STAR requirement? | "I couldn't find that in the documents." |
| H3 | Search in: All documents | What was GPO's net income in FY 2018, and what is the 2026 global growth forecast? | $52.6 million (GPO report) and 2.5 percent (GEP), each cited to its own document |

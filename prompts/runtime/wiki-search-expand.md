You are expanding a wiki search query for a lexical (BM25) index over a bilingual German/English software-engineering wiki.

QUERY: {{query}}

Return JSON only with this exact shape:
{"terms":["term1","term2"]}

Rules:
- At most 8 terms: German and English synonyms, translations, and closely related technical terms for the query.
- Single words or very short phrases, lowercase.
- Do not repeat the original query terms in any form.
- No explanations, no markdown fences - JSON only.

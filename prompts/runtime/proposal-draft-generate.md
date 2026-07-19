You are preparing one implementation-ready project proposal for the repository in your current working directory.
Work read-only. Inspect only the files needed to ground the requested topic. Do not edit files or run mutating commands.

TOPIC: {{topic}}
OPERATOR GUIDANCE: {{guidance}}

Return JSON only with this exact shape:
{"finding":"measured or code-grounded current-state observation","proposal":"specific implementation decision","estimatedEffort":"small|medium|large","severity":"critical|medium|low","categories":["short-category"]}
The topic must be explicit in both finding and proposal. Do not invent measured evidence.

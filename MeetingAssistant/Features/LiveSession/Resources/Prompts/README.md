# Vendored ai_work prompts

These prompts are vendored from `../ai_work/prompts` so the backend container can load deterministic post-meeting prompts without requiring a sibling repository mount at runtime.

Source files:

- `meeting_summary_prompt.txt` -> `MeetingSummarizer.md`
- `task_extraction_prompt.txt` -> `TaskExtraction.md`
- `PersonalizedSummary_prompt.txt` -> `PersonalizedMeetingSummarizer.md`

Sync strategy:

1. When `../ai_work/prompts/meeting_summary_prompt.txt`, `../ai_work/prompts/task_extraction_prompt.txt`, or `../ai_work/prompts/PersonalizedSummary_prompt.txt` changes, copy/adapt the updated file into this directory in the same commit that updates backend prompt tests.
2. Keep the `{transcript}` placeholder unchanged; backend code fills it exactly before making the LLM request.
3. `TaskExtraction.md` follows the ai_work JSON-array contract (`task`, `responsible_person`, `deadline`). Backend parsing also tolerates the older `{ "tasks": [...] }` object shape for compatibility with stale/mocked LLM responses.
4. Personalized summaries additionally use `{participant}` and `{personalization_context}`. The backend fills `{personalization_context}` with only values that exist for the participant.
5. Run the prompt-focused backend tests after every sync to prove the vendored content is loaded and placeholders are replaced.

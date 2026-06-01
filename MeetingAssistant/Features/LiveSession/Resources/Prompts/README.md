# Vendored ai_work prompts

These prompts are vendored from `../ai_work/prompts` so the backend container can load deterministic post-meeting prompts without requiring a sibling repository mount at runtime.

Source files:

- `meeting_summary_prompt.txt` -> `MeetingSummarizer.md`
- `task_extraction_prompt.txt` -> `TaskExtraction.md`

Sync strategy:

1. When `../ai_work/prompts/meeting_summary_prompt.txt` or `../ai_work/prompts/task_extraction_prompt.txt` changes, copy the updated file into this directory in the same commit that updates backend prompt tests.
2. Keep the `{transcript}` placeholder unchanged; backend code fills it exactly before making the LLM request.
3. Run the prompt-focused backend tests after every sync to prove the vendored content is loaded and the placeholder is replaced.

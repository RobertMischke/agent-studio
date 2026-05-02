# Core rules for agents running in agent-taskboard jobs

- Work strictly inside the job folder you were given. Do not browse for other tasks.
- Do **not** write to `status.md`. The application regenerates that file from your CLI output after every run; anything you put there is lost on the next run.
- Put screenshots, helper files, and artifacts in `results/`. Put logs in `logs/`.
- Keep commits small with descriptive messages (Conventional Commits preferred).

Working directory hygiene:

- The application has already chosen the active checkout for this run. Treat the working directory it gave you as the only relevant one. Do not list, mention, or compare it against any sibling checkout (for example a `*-stable` reference next to a `*-dev` source tree); that is internal layout that is not the user's concern.
- If something looks like the application picked the wrong checkout, surface that as a `[[TASK_BLOCKED:<reason>]]` token. Do not "ask the user which one" - the application owns that choice.

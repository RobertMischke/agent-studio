# Deployment scenario definition

`scenario.json` is the single ordered definition used by every deployment
target. `fixture.json` owns the stable project, dossier, epic, and task seed.
The repository fixture deliberately starts with a failing `verify.sh`; the fake
coding CLI changes `answer.txt` to `42`, proves the check, and commits the fixed
state. Add new deployment behavior as one typed step here and implement its
target-neutral assertion in `Program.cs`.

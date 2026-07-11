# MVP presentation capture runbook

This runbook produces the 16:9 product media used by the MVP slide deck. Use
only the deterministic ADR-0056 workspace. Never capture the production
workspace, even for a quick rehearsal.

The source of truth for the ordered outputs is the
[MVP presentation storyboard](../../product/mvp-presentation-storyboard.md).

## Safety gate and demo reset

Before any capture, confirm all of the following:

- The visible project picker contains only `Demo App` and `Demo Platform`.
- The cards use `DEMO-*` or `PLAT-*` keys. Stop immediately if any real project
  or task name appears.
- Browser notifications, email, chat overlays, password managers, and desktop
  notifications are closed or suppressed.
- The output folder contains no earlier production capture.

Reset the default demo store from the repository root:

```sh
node scripts/seed-demo-workspace.mjs
```

The seed refuses the production workspace root. After reset, open the demo
backend configured for `C:\Projects\agent-taskboard-workspace-demo`, select
`Demo App`, and verify the visible keys again. Do not point ScreenToGif or OBS
at the stable or production browser window.

## Deterministic stills with Playwright

From the repository root, one command resets a temporary demo store, starts an
isolated dynamic-port backend and frontend, captures every still in both
themes, and tears the stack down:

```sh
npm --prefix frontend run docs:presentation
```

The command uses marketing mode, a 1920x1080 CSS viewport, and a device scale
factor of 2. Each PNG is therefore 3840x2160 and scales cleanly into a
1920x1080 slide. Outputs are written to
`docs/assets/images/presentation/`. Do not resize the source files before
placing them in the deck. Fit them proportionally inside the slide frame.

The filenames include their order, theme, and `--real` source claim. A failed
dimension check fails the Playwright run instead of leaving a soft screenshot
in the output set.

## Silent loops with ScreenToGif

Use ScreenToGif only for storyboard rows marked as a silent loop.

1. Reset and verify the demo workspace using the safety gate above.
2. Set the browser window to 1920x1080 and 100 percent browser zoom. Keep the
   application full frame visible. Record one theme per loop as named in the
   storyboard.
3. In ScreenToGif Recorder, select a 1920x1080 capture region and 15 fps. Turn
   audio off. Keep the webcam absent.
4. Enable cursor capture. Use a restrained high-contrast cursor halo and click
   ring. Keep the pointer still when the viewer should read.
5. Rehearse the path once, reset the starting surface, then record 6 to 10
   seconds. Pause for about one second at the first and last states so the loop
   is readable.
6. In the editor, remove setup frames, accidental hover noise, and dead time.
   Keep the interaction at normal speed. Add no narration or baked-in caption.
7. Export GIF for maximum slide compatibility. If the deck supports embedded
   MP4 loops, also export H.264 at 1920x1080 and 15 fps using the same basename.

Use the exact storyboard filename, including theme and `--real`. Review the
loop at slide size and confirm it restarts without a distracting jump.

## Narrated backup with OBS Studio

The narrated backup is optional and contains microphone audio but no webcam.

1. Create an OBS scene named `MVP Demo Only` with one Window Capture source for
   the verified demo browser. Do not use Display Capture when Window Capture is
   available.
2. Add one microphone input. Remove or disable every Video Capture Device and
   webcam source. Disable desktop audio to avoid notifications or meeting audio.
3. Set Base Canvas and Output Resolution to 1920x1080, Common FPS to 30, and
   output to MKV using the high-quality recording preset. MKV protects the take
   if recording is interrupted.
4. Set the browser to 1920x1080 at 100 percent zoom. Use the dark theme named in
   the storyboard. Keep the cursor visible with the operating-system cursor
   emphasis enabled, but do not add a large novelty cursor.
5. Reset the demo, rehearse the storyboard from start to finish, then record a
   60 to 90 second take. Leave two seconds of silence at both ends.
6. Check microphone level before the take. Peaks should remain below 0 dB and
   normal speech should sit roughly between -18 dB and -6 dB.
7. Use OBS `File > Remux Recordings` to create the final MP4. Name it exactly as
   listed in the storyboard. Confirm that it has microphone audio, no desktop
   audio, no webcam, and no production data before sharing it.

## Final quality check

- Every still exists in both themes and is exactly 3840x2160.
- Silent loops contain no audio or webcam and use the requested theme.
- The narrated backup is 1920x1080 at 30 fps, microphone only, with no webcam.
- Captions live in the deck, not baked into product media.
- Every filename matches the storyboard and contains `--real`.
- A second person checks the first and last frame of every motion asset for
  production data before it leaves the operator machine.

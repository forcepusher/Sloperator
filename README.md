# Hands-off frame check (feature/hands-off, Windows)

Debug app run against a local stand-in model server: mouse_move to the screen middle, wait 3 s,
final text. Captured from outside Sloperator with BitBlt + CAPTUREBLT. Other apps' content is
blanked grey; only the Sloperator window and the 4 px frame band are kept.

- 1-screen-while-working.png: primary screen 1:1 (2560x1080) during the wait; frame on all four edges.
- 2-window-waiting-for-you.png: the tool card while an untagged mouse move held the action back.
- 3-screen-after-task.png: after the task; no frame (band kept in the top-left corner only).
- 4-window-after-task.png: the finished chat.
- corner-*-64.png: top-left 64x64 at 1:1, beyond the 4 px band blanked; corner-*-x8.png the same enlarged 8x.

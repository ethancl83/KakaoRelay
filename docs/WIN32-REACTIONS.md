# Win32 reaction feasibility · 2026-09-29

Validated on the installed Windows KakaoTalk build, without Computer Use input.

- Individual message bubbles and inline reaction buttons were not exposed as distinct HWNDs.
- Chat history uses EVA_VH_ListControl_Dblclk (control ID 100).
- SetCursorPos followed by WM_MOUSEMOVE / WM_LBUTTONDOWN / WM_LBUTTONUP opened the reaction picker. Posting the same mouse messages without aligning the real pointer did not open it in this test.
- The picker is a separate EVA_Window. GetWindow(GW_OWNER) identified the selected conversation window.
- The reaction grid is EVA_Window_Dblclk, control ID 1001. Its search box is a native Edit control.
- A single red-heart selection through the grid HWND succeeded. PrintWindow captured a heart count of 1 under the intended trigger message. No AI computer-use actions were used for this test.

This proves native reaction dispatch, not automatic message-ID-to-screen-position mapping. Coordinates were visually verified for this one test; they must not be reused as fixed production coordinates. New messages, wrapping, scrolling, DPI, recent reactions and picker layout can move targets. The live auto-reply feature is unchanged and does not automatically add hearts yet.

A production implementation must bind the target message ID to a verified current visible message, validate conversation identity and popup ownership, and serialize cursor/popup work across rooms. It must avoid blind retries because reaction selection may toggle an existing reaction off. Win32-only does not imply background operation: this tested path moves the real cursor.

Local probe source: .tools/reaction-probe/Program.cs (Git-ignored experiment). Native screenshots: artifacts/qa/native-*.png (Git-ignored).

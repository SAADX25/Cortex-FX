# Floating image and PDF tools

Drag a file while holding **Alt** (the default tools modifier), then release it over an icon. The action name appears in the wheel's center on hover or keyboard selection. Left/Right or Tab selects an action; Enter runs it. Format and quick-action wheels also use icons.

- **Compress images / PDFs:** choose a reduction percentage or a maximum size in KB/MB. For batches, a percentage is applied to each original and a maximum size is a per-file limit. Outputs are verified against the limit before publication. If a target cannot be met, no oversized replacement is saved; choose a larger limit or resize first.
- **PDF compression:** preserves text and interactive content by default. The optional **Allow rasterized PDF pages** setting explicitly permits flattening pages to images, removing selectable text, links, forms, and layers. Page count and physical dimensions are retained.
- **Crop image:** select one still image. Drag any of eight handles, move the selection, choose Free/1:1/16:9/9:16/4:3/3:4, or type pixel dimensions. Arrow keys move by one pixel and Shift by ten. Reset restores the full image. The preview and crop use EXIF-correct orientation. Animated and multi-frame inputs are rejected rather than flattened.

All tools save uniquely named sibling copies. Originals remain in place. Closing a customization window cancels that action. The crop/target size options are per-action and do not change global wheel preferences.

Folders, missing files, non-file drag data, and selections without any available actions are ignored and dismiss the wheel without an error. Rejected input clears the previous selection, and a subsequent supported drag works immediately. Quick actions still accept regular files for ZIP, copy paths, and SHA-256, even when they have no conversion format.

The crop editor uses a compact portrait layout with a native Windows desktop-acrylic backdrop, rounded corners, a segmented aspect-ratio selector, and fixed bottom actions. The image and crop handles remain sharp. If the system cannot provide acrylic, the editor uses an opaque surface. The window fits the available work area and remains resizable. Acrylic follows Windows transparency, activation, and power-saving preferences.

Native backdrop reference: [Microsoft DWM system backdrop types](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type).

The compression dialog uses the same native acrylic surface as the crop editor. Percentage and maximum-size modes are visible as two selectable cards; the output limit updates immediately, and invalid sizes disable compression without retaining a stale limit. A scrollable options area keeps the action button visible on smaller screens. PDF rasterization remains an explicit opt-in.

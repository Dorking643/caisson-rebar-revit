# Start from a concrete shell

[中文](QUICKSTART.zh-CN.md) · [Home](../README.md)

This guide targets **R36.Share1 / Revit 2025**. The numbers below are teaching inputs for learning the editor. The three supplied RVT files remain unreinforced; use working copies for this tutorial.

## Prepare

1. Extract the repository or download package. Close Revit and follow the [installation instructions](../installer/README.md).
2. Copy `models/Wall.rvt` or `models/Slab.rvt` into a working directory and open the copy in Revit 2025.
3. Open **Caisson Reinforcement → Model Creation → Wall** or **Slab** on the ribbon.
4. Use **Wall** for the wall-only model and **Slab** for the slab-only model. Both commands can be used separately in `Combined.rvt`. Keep the host marks `CJ-WALL` and `CJ-SLAB`.

## Example 1: three rings along the wall height

Open **Wall** in a working copy of `Wall.rvt`.

1. Select **HRB400 Φ16** in **Bar Types**.
2. Open **Cover**, set **Inner / Outer / Top / Bottom** to **40 mm**, and click the cover row's **Apply** button.
3. Click **Ring**, then click inside **Radial Section** to place an initial ring. Select it in **Placed Bars**.
4. Enter the position below in **Position · mm**, then click that section's **Apply** button.
5. In **Distribution**, check **Enable**, enter the distribution settings, and click its **Apply** button.

| Section | Field | Teaching input |
| --- | --- | ---: |
| Position · mm | x | 225 |
| Position · mm | y | 300 |
| Distribution | Direction | Along y |
| Distribution | Spacing | 150 |
| Distribution | Count | 3 |

Wall section coordinates start at the top inside corner: x points outward and y downward. With an inside radius of 2250 mm, the ring centreline radius is **2475 mm**. The three local elevations relative to the wall top are **−300, −450, and −600 mm**. `Count` includes the original ring. The rings overlap in plan because their radii are equal; the section shows three separate positions.

Wait for the editor check to finish and confirm **3 bars**, then click **Create Rebar**. Inspect the three rings in the Revit working copy and save that copy.

![Wall editor with the teaching inputs](images/wall-editor-preview.png)

*Offline render of the actual R36.Share1 WinForms controls using the tutorial inputs.*

## Example 2: three concentric rings at one slab elevation

Open **Slab** in a working copy of `Slab.rvt`.

1. Select **HRB400 Φ16** in **Bar Types**.
2. Open **Cover**, set **Top / Bottom / Side** to **40 mm**, and click the cover row's **Apply** button.
3. Click **Ring**, then click in **Plan** away from the centre to place an initial ring. Select it in **Placed Bars**.
4. Enter the values below in **Position · mm**. These slab fields update the preview automatically; there is no additional position Apply button.

| Field | Teaching input |
| --- | ---: |
| Name | Tutorial Rings |
| Enabled | Checked |
| Elevation Z | −100 |
| Count | 3 |
| Radius | 500 |
| Radial step | 200 |

Slab Z is measured from the central top surface, positive upward. This example produces centreline radii of **500, 700, and 900 mm**, all at **Z = −100 mm**. The plan should show three concentric rings and the footer should report **3 bars / 0 pending**.

After the editor check completes, click **Create Rebar**, followed by **Show Model**. Inspect the result, then use **Save Model** or Revit's normal save operation to save the working copy.

![Slab editor with the teaching inputs](images/slab-editor-preview.png)

*Offline render of the actual R36.Share1 WinForms controls using the tutorial inputs.*

## Common controls

- Scroll to zoom, drag the middle mouse button to pan, and double-click a canvas to fit the view.
- `Esc` ends placement and returns to selection; `Delete` removes the selected draft or group; `Ctrl+Z` undoes editor changes.
- Click **Apply** after changing cover values. If an action is unavailable, read the footer status and the right-side message first.
- Each example can be completed in its individual host model; merging the three RVT files is unnecessary.

The delivered DLL was used to check both tutorial inputs offline: each produced 3 rebar paths with 0 blocking items. No reinforcement was written to the supplied RVT files. The images show offline forms; native Revit creation is performed in working copies using the steps above. [Preview and input-check record](images/preview-verification.json)

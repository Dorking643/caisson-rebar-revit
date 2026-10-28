# Editor preview assets

`wall-editor-preview.png` and `slab-editor-preview.png` are direct `DrawToBitmap` renders of `WallReviewForm` and `SlabReviewForm` from the distributed **R36.Share1** assembly. They show the teaching parameters documented in the quickstart guides.

The forms were instantiated in an isolated Windows Forms harness. No Revit session, add-in installation, model transaction, or native rebar creation was used. The supplied shell RVT files were not changed. The images are offline UI previews, not screenshots of a live Revit modelling run.

`preview-verification.json` records the corresponding offline parameter and geometry checks. The image dimensions are 1496 × 909 px. The generator used the shipped assembly directly and did not modify its UI controls or images to create new interface content.

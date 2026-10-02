# List Hero visual direction

The shared UI uses a warm paper background, blue and red superhero accents, yellow callouts, dark comic-panel outlines and offset shadows. Display lettering appears in headings; normal system text stays in forms and longer descriptions. Controls retain clear labels, keyboard focus, validation and existing workflows. Small-screen layouts stack panels; reduced-motion preferences disable transitions. The brand assets and CSS live in ListHero.UI so another Blazor host can reuse them.

## Mascot

Active asset: `src/ListHero.UI/wwwroot/images/list-hero-mascot-final-v1.png`. The current version combines the second fresh render's pose with the third fresh render's baggy costume and a uniform-width pencil shaft. Created with the built-in image-generation tool using those two references; alpha transparency preserved. Earlier assets and prompts remain available for comparison.

Initial prompt:

```text
Use case: logo-brand
Asset type: transparent mascot illustration for List Hero, a whimsical but professional wishlist web app.
Primary request: a nerdy looking skinny adult dude with oversized thick nerd glasses wearing a noticeably baggy superhero costume with a classic Superman feel: blue suit, red cape, red boots, yellow belt, but his own distinct chest badge containing exactly "LH". He is enthusiastically making a note in a small notebook using an absurdly large comically HUGE yellow wooden pencil, almost as long as his body. The pencil must be the instantly obvious visual joke.
Style: polished contemporary comic-book illustration, bold dark ink outlines, expressive friendly face, controlled flat colors with a little halftone shading. Charming awkward civilian dressed as a superhero, skinny limbs, oversized sleeves and saggy costume, not muscular. Neatly readable at website size.
Composition: one isolated full-body character, notebook and huge pencil fully visible, slight three-quarter stance; compact portrait composition, generous transparent margin. Cape follows silhouette. No background, no backdrop shape, no shadow ground, true alpha transparency.
Text: only "LH" on his unique chest emblem, no other lettering.
Constraints: original mascot design with classic superhero inspiration; no Superman S emblem, no watermark, no extra people, avoid photorealism and 3D. Make the drawing appealing and finished.
```

Pencil placement edit prompt (version 3, referenced the original first image):

```text
Edit the ORIGINAL first List Hero mascot provided. Preserve the entire original character, face, glasses, hair, outfit, LH badge, cape, notebook, body proportions, original enormous yellow pencil and its angle and all illustration style. User correction: take that first pencil and simply MOVE IT UP along its existing diagonal axis toward the upper left, so the sharpened wooden point and black graphite tip are FULLY VISIBLE ABOVE the notebook with a small clear visible gap. There must be NO overlap between pencil tip and notebook. Do not put the pencil behind the notebook; do not hide any of its point. Shift pencil up only enough to eliminate the overlap. Keep pencil's original huge scale, recognizable proportions, and complete eraser within image. Adjust the writing hand only as needed to follow this small reposition and improve natural grip: palm behind the shaft, fingers curl around it, thumb opposed on side; keep the original lower grip position relative to shaft rather than gripping near shoulder. The hand must look like it holds the shaft rather than a palm pasted on it. Everything else unchanged. Genuinely transparent background preserved. No background glow or new objects.
```

Full-grip edit prompt (version 4, referenced the first grip revision of version 3):

```text
Edit ONLY the pencil-holding hand in this mascot. Keep the pencil absolutely fixed: same placement, angle, scale, tip and eraser; keep notebook and everything else unchanged.
Make this a full, convincing tree-branch grip. Enlarge the holding hand enough that it spans the ENTIRE pencil diameter at the grip point. At the moment the fingertips stop halfway across the shaft; fix that specifically: four bent fingers must extend ALL THE WAY across the visible yellow shaft to its right contour and curl AROUND that right contour, with fingertips disappearing behind it. The fingers must cover the shaft across its whole width at their contact band, not leave a strip of yellow pencil between fingertips and outer edge. Draw the thumb clearly on the opposite LEFT contour of the pencil, curved around it in opposition. Show thumb pad grasping left edge and four rounded knuckles/fingers grasping right edge. Natural closed cylindrical power grip like tightly holding a chunky branch, not a writing pinch or fist floating on one side. Hand remains connected naturally to wrist. Exactly one thumb and four fingers, no extras.
Preserve character, pencil position and proportions, comic linework, face, colors, cape, LH emblem, notebook and full-body framing. Preserve alpha transparency.
```

Thumb edit (version 5): the visible thumb is hidden behind the pencil; the four curled fingers and pencil placement are retained. Built-in image-generation edit using the user's attached version 4 image.

```text
Precise local edit of this exact mascot. Remove ONLY the visible thumb of the pencil-holding hand: it is the small upward-curving flesh-colored digit at the TOP LEFT of the holding fist, lying on the yellow pencil around x470-530 y300-355. The thumb should be on the far side, completely hidden behind the pencil shaft. Restore the continuous yellow pencil surface and its original black contour where the thumb currently shows. Keep all FOUR visible curled fingers exactly as they are; do not remove or redraw a finger. Keep hand and wrist position, full grip, pencil location/angle/size and point, notebook, other hand, face, glasses, hair, LH suit, cape, legs and boots unchanged. Everything else identical to input. Preserve actual transparent background and full-body composition. No visible thumb, no extra fingers, no new background.
```

Hand angle edit (version 6): slight counterclockwise tilt of the holding hand; hidden thumb and pencil placement retained. Built-in image-generation edit referencing version 5.

```text
Precise small edit of the supplied List Hero mascot. Rotate ONLY the pencil-holding hand (viewer left, across his chest) slightly COUNTERCLOCKWISE in the image plane, about 8 to 12 degrees, to create a more natural wrist angle. Treat the four curled fingers and back of hand as the same gripping form, subtly tilted together counterclockwise around the center of the grip; top of hand leans a little further left and wrist connection a little right. Blend the immediate wrist/sleeve cuff connection naturally. Keep a firm full grip enclosing the shaft. The thumb stays on the far side of the pencil and MUST REMAIN COMPLETELY HIDDEN; four visible curled fingers only.
PENCIL ABSOLUTELY FIXED: do not rotate, move, resize or reshape it. Same eraser, yellow shaft and fully visible wooden/graphite point positions as input, same clear gap above notebook. Keep the hand at this same overall grasping location on the shaft, just the slight counterclockwise tilt.
All other pixels/design should remain as close as possible to original: face, hair, glasses, expression, notebook and other hand, baggy costume, LH emblem, cape, legs and boots. Preserve full-body framing, original proportions, comic linework/colors and alpha transparency. No visible thumb, extra finger or additional objects.
```

Pinky edit (version 7): extend the lowest visible finger to curl around the pencil's far edge. Built-in image-generation edit referencing version 6.

```text
Surgical local edit: ONLY lengthen and curl the PINKY of the pencil-holding hand. The pinky is the LOWEST of the four visible curled fingers on the hand at viewer left, across the character's chest. Its fingertip currently stops short of the far/right edge of the thick yellow pencil. Extend JUST this lowest finger slightly toward the right, enough to reach past the pencil's right contour and visibly curl around that contour. Its fingertip should disappear behind the far edge, conveying a full grasp around the entire pencil diameter. Maintain natural skinny pinky anatomy, a subtle extension only.
Preserve exactly the other three fingers, the back of hand, wrist position, and current slight counterclockwise hand tilt. Thumb remains entirely hidden behind the pencil. Do not add a finger or show a thumb. Do not change overall grip or enlarge the whole hand.
Pencil is absolutely locked: identical placement, angle, length, thickness, eraser and point. Preserve notebook, supporting hand, face, glasses, hair, LH suit, cape, boots, body and comic style. Everything else unchanged; preserve genuine alpha transparency and full-body framing. Only the lowest finger's reach and curl change.
```

## Fresh render

The preceding fresh mascot was regenerated from scratch with the built-in image-generation tool, without any referenced images, to replace the deteriorated linework of the iterative edits. It includes a visibly oversized costume, fuller sleeves and trousers, and a raised pencil with its point clear of the notebook. The earlier assets remain available for comparison.

Final fresh-render prompt:

```text
Create a brand-new clean comic-book mascot illustration for List Hero. Transparent PNG, portrait 2:3, full body with generous margins. Crisp consistent black ink, smooth flat red/blue/yellow colors and tidy cel shading, no distressed texture, fuzziness or patchy paint.

A friendly skinny adult man with messy brown hair and oversized thick black nerd glasses, cheerful awkward smile, skinny limbs, an unmistakably oversized, loose and baggy blue superhero costume, long red cape, loose red boots and yellow belt. Classic Superman feel with his own yellow/red chest emblem reading exactly "LH" (not S). Not muscular. Original character.
COSTUME FIT IS IMPORTANT: the skinny man is wearing a superhero suit visibly a couple sizes too big. Roomy loose sleeves with fabric hanging below his forearms and bunching at wrist cuffs; slack torso with folds blousing over the yellow belt; baggy trouser legs with generous fabric at hips and knees, puddling a little at the red boots. His skinny neck and hands contrast humorously with the roomy outfit. Do NOT draw skin-tight superhero spandex or outlined muscles. Keep the costume charming and readable rather than messy. Cape and boots remain classic.

He is poised to make a note in a little spiral notebook with a comically huge yellow wooden pencil. IMPORTANT PROP GEOMETRY: notebook held in his left hand (viewer right) at LOWER CHEST HEIGHT, tilted toward him, at the center-right of his torso. The huge pencil runs diagonally from TOP LEFT to LOWER RIGHT across his UPPER CHEST, with pink eraser high above his left shoulder and FULLY VISIBLE sharpened wooden/graphite tip ending at CHEST HEIGHT just ABOVE the notebook's upper edge. A small visible air gap separates tip and notebook; neither overlaps the other. It looks like he is about to write. The tip must NOT extend down to his waist, belt, hip or legs. In a 1024x1536 composition, loosely aim eraser around (220,60), pencil tip around (680,600), and notebook top around (720,650). Pencil about 100 pixels thick and 750 pixels long: absurdly huge but all of it fits above the belt.

His right hand (viewer left) grasps the pencil near his chest as if holding a thick tree branch, in a believable closed cylindrical POWER GRIP. Back of holding hand faces viewer, four curled fingers span the WHOLE shaft diameter and curl around its right edge, including the lowest pinky, whose tip reaches fully around the far edge instead of stopping short. Thumb is entirely behind the pencil and NOT VISIBLE. Four visible fingers, no extra digits. Natural hand and wrist, gently tilted counterclockwise about 10 degrees. Not a writing pinch, floating fist or flat palm.

Keep character and props cohesive, clearly readable and polished for a professional but whimsical website. Full eraser, tip, notebook, cape and both boots visible. Actual transparent background, no scenery, ground, glow, captions or watermark. Only lettering is "LH" on his chest.
```

## Combined final version

Built-in image-generation tool, using two references: the second fresh render for pose and character, and the third fresh render for baggy costume fit. Pencil shaft corrected to match the lower section's width throughout.

Final combination prompt:

```text
Create a clean final List Hero mascot combining TWO supplied references.
REFERENCE IMAGE 1 = POSE AND CHARACTER reference (the user's preferred second render). Use its stance, body orientation, head, face, glasses, expression, arm positions, high pencil-holding grip, notebook position and diagonal pencil placement.
REFERENCE IMAGE 2 = COSTUME FIT reference (the user's preferred third render). Transfer its unmistakably oversized BAGGY blue costume: big roomy sleeves with drooping fabric, slack torso blousing above yellow belt, generous loose trousers bunched around knees and boots. Keep the skinny civilian inside the outfit, not a muscular superhero.

CRITICAL PENCIL CORRECTION: In reference 1 the yellow shaft ABOVE the holding hand is too wide. NARROW ONLY THIS UPPER PORTION to match the existing shaft thickness immediately BELOW the hand. The pencil must be a straight uniform-width hexagonal wooden pencil along its entire yellow painted body: long side contours STRAIGHT AND PARALLEL and continuous through the occluded hand region. No widening toward eraser, no taper in the yellow shaft, no kink or bulge at the grip. Keep reference 1's yellow shaft width BELOW the hand as the target width for the whole shaft. Ferrule and pink eraser sized consistently to the narrower shaft. Only the sharpened exposed wood and graphite at the tip taper. Maintain hilariously oversized LENGTH and the same diagonal angle and tip placement. Entire sharpened point visible with a clear gap above notebook, no overlap.

Preserve reference 1's natural whole-hand branch-style grip, subtle counterclockwise hand tilt, FOUR visible curled fingers including pinky reaching completely around the shaft, thumb hidden behind the pencil. Adjust grip contact only as needed for uniform shaft. Keep notebook hand natural. Chest emblem reads exactly "LH"; classic blue suit/red cape/red boots/yellow belt, no Superman S.
Render all of this with fresh crisp consistent black comic ink, smooth flat colors and tidy cel shading, no fuzzy linework, smeared or patchy skin or weathered textures. Complete full-body composition, cape and both boots inside frame with a little margin. Genuine transparent background with clean alpha edges, no backdrop glow, floor, captions or watermark. Preserve the second render's pose, combine the third render's BAGGY OUTFIT and correct pencil geometry.
```

## Verification

The existing owner/guest browser regression captures optional desktop (1440px) and phone (390px) screenshots for the home page, empty lists, owner list, guest fulfillment and edit form. It checks for horizontal overflow at both widths. Set `LISTHERO_UI_REVIEW_DIR` to an artifact directory to save the screenshots. Test accounts and SQL data are isolated from normal development data.

Verified October 2, 2026: Release build with no warnings or errors; full 150-test run passed with no skips. After the final keyboard skip-link styling adjustment, the browser journey passed again. Ten desktop/phone screenshots were reviewed under `.artifacts/ui-overhaul/`. A two-pixel phone overflow from the rotated decorative backdrop was corrected.






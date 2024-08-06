# AutoCrop

AutoCrop automatically crops images down to a smaller size during capture and puts them into a subfolder of the existing capture path. This operation happens before the larger image is finalized, thus the cropped version may arrive before the full size image is committed to disk. Currently only supports fits files.  It may not work with DSLRs.
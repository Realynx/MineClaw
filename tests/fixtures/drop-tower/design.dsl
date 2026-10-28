```
blueprint mob drop tower
size 16x16x16
facing south
palette C=cobblestone H=slab:cobble D=wooden_door:south
# shaft legs: bottom 3 layers open so mobs drop onto Fox's 8x8 floor
pillar C 6,0,6 3
pillar C 9,0,6 3
pillar C 6,0,9 3
pillar C 9,0,9 3
# 2x2 drop shaft (inside air), centred
walls C 6,3,6 9,11,9
# dark spawning platform with shaft hole
floor C 0,12,0 15,12,15
clear 7,12,7 8,12,8
# 2-high spawning space walls
walls C 0,13,0 15,14,15
# slab roof
roof H flat 0,15,0 15,15
# access door to platform (water channels to be

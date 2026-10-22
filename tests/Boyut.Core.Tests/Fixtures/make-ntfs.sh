#!/bin/bash
# Builds the NTFS test image used by NtfsMftReaderTests (needs ntfs-3g + FUSE, run as root).
set -e
IMG=${1:-ntfs-sample.img}
M=$(mktemp -d)
rm -f "$IMG"; truncate -s 48M "$IMG"
mkntfs -F -q -f -c 4096 -L BoyutTest "$IMG" >/dev/null 2>&1
ntfs-3g -o streams_interface=windows "$IMG" "$M"
mkdir -p "$M/Users/Ali/Downloads" "$M/Users/Ali/Documents" "$M/Projects/web/node_modules/lib" "$M/many"
head -c 5242880 /dev/zero > "$M/Users/Ali/Downloads/big.bin"
printf 'hello' > "$M/Users/Ali/Downloads/tiny.txt"
head -c 307200 /dev/zero > "$M/Users/Ali/Documents/report.pdf"
ln "$M/Users/Ali/Documents/report.pdf" "$M/Users/Ali/Documents/report-link.pdf"
head -c 1024 /dev/zero > "$M/Users/Ali/Documents/report.pdf:Zone.Identifier"
head -c 51200 /dev/zero > "$M/Projects/web/node_modules/lib/index.js"
echo '{"name":"web"}' > "$M/Projects/web/package.json"
truncate -s 104857600 "$M/sparse.dat"
head -c 2000 /dev/zero > "$M/Çalışma Belgesi ğüşiöç.docx"
for i in $(seq -w 0 2999); do printf "%s" "$i" > "$M/many/f$i.txt"; done
echo gone > "$M/deleted.txt"; rm "$M/deleted.txt"
sync; umount "$M"; rmdir "$M"

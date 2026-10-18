"""Draws src/LzFilm.App/lzfilm.ico: a film strip whose frames get shorter (compression),
squeezed by two amber arrows. Icon design: Claude (Anthropic).
Usage: python tools/make-icon.py   (needs Pillow; writes lzfilm.ico in the current folder)"""
from PIL import Image, ImageDraw
S=1024
def lerp(a,b,t): return tuple(int(a[i]+(b[i]-a[i])*t) for i in range(3))
def make(simple=False):
    img=Image.new("RGBA",(S,S),(0,0,0,0))
    # background gradient in rounded square
    bg=Image.new("RGBA",(S,S))
    top,bot=(18,72,99),(9,38,56)
    d=ImageDraw.Draw(bg)
    for y in range(S):
        d.line([(0,y),(S,y)],fill=lerp(top,bot,y/S)+(255,))
    mask=Image.new("L",(S,S),0)
    m=40
    ImageDraw.Draw(mask).rounded_rectangle([m,m,S-m,S-m],radius=210,fill=255)
    img.paste(bg,(0,0),mask)
    dr=ImageDraw.Draw(img)
    cream=(245,236,214,255); hole=(12,48,68,255); amber=(244,166,52,255); frame=(31,104,128,255)
    # film strip
    x0,x1=300,724; y0,y1=150,874
    dr.rounded_rectangle([x0,y0,x1,y1],radius=36,fill=cream)
    # sprocket holes
    if not simple:
        hw,hh=46,52; gap=(y1-y0-60)//8
        for i in range(8):
            yy=y0+40+i*gap
            for xx in (x0+22,x1-22-hw):
                dr.rounded_rectangle([xx,yy,xx+hw,yy+hh],radius=12,fill=hole)
    # frames: decreasing height = compression
    fx0,fx1=(x0+92,x1-92) if not simple else (x0+60,x1-60)
    heights=[250,150,80] if not simple else [260,150,80]
    gaps=[36,30,26]
    y=y0+56
    for h,g in zip(heights,gaps):
        dr.rounded_rectangle([fx0,y,fx1,y+h],radius=22 if h>100 else 14,fill=frame)
        last=(y,y+h)
        y+=h+g
    # amber squeeze arrows at the bottom pointing inward
    cy=(last[0]+last[1])//2
    a=95 if not simple else 120
    dr.polygon([(x0-50-a*1.5,cy-a),(x0-24,cy),(x0-50-a*1.5,cy+a)],fill=amber)
    dr.polygon([(x1+50+a*1.5,cy-a),(x1+24,cy),(x1+50+a*1.5,cy+a)],fill=amber)
    return img
big=make(False); small=make(True)
sizes=[256,128,64,48,32,24,16]
imgs=[(big if s>=48 else small).resize((s,s),Image.LANCZOS) for s in sizes]
imgs[0].save("lzfilm.ico",format="ICO",sizes=[(s,s) for s in sizes],append_images=imgs[1:])

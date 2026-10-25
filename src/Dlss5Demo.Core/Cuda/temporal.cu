// Optical flow is estimated by NVOFA. These kernels keep its preparation,
// confidence checks and correction history on the device (SDR RGBA8).
__device__ float lerp_exact(float a,float b,float t,int fused) {
    return fused ? __fmaf_rn(a,1.f-t,b*t) : a*(1.f-t)+b*t;
}
__device__ float3 rgb(const unsigned char* p) {
    return make_float3(__fdiv_rn((float)p[0],255.f),__fdiv_rn((float)p[1],255.f),__fdiv_rn((float)p[2],255.f));
}
__device__ float3 sample(const float4* image,float x,float y,int w,int h,int fused) {
    int x0=(int)x,y0=(int)y,x1=min(x0+1,w-1),y1=min(y0+1,h-1); float ax=x-x0,ay=y-y0;
    float4 a=image[y0*w+x0],b=image[y0*w+x1],c=image[y1*w+x0],d=image[y1*w+x1];
    return make_float3(lerp_exact(lerp_exact(a.x,b.x,ax,fused),lerp_exact(c.x,d.x,ax,fused),ay,fused),
        lerp_exact(lerp_exact(a.y,b.y,ax,fused),lerp_exact(c.y,d.y,ax,fused),ay,fused),
        lerp_exact(lerp_exact(a.z,b.z,ax,fused),lerp_exact(c.z,d.z,ax,fused),ay,fused));
}
__device__ bool valid(float x,float y,int w,int h) { return isfinite(x)&&isfinite(y)&&x>=0&&y>=0&&x<=w-1&&y<=h-1; }
extern "C" __global__ void gray(const unsigned char* input,int pitch,unsigned char* output,int op,int w,int h,int aw,int ah) {
    int x=blockIdx.x*blockDim.x+threadIdx.x,y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x>=aw||y>=ah)return;
    const unsigned char* p=input+min(y,h-1)*pitch+min(x,w-1)*4;
    int s=77*p[0]+150*p[1]+29*p[2]; output[y*op+x]=(s+127+((s>>8)&1))>>8;
}
extern "C" __global__ void expand(const unsigned char* packed,int pp,const unsigned char* costs,int cp,
    float2* motion,unsigned char* rejected,int w,int h,int gw,int gh,int grid,int confidence,int clear) {
    int x=blockIdx.x*blockDim.x+threadIdx.x,y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x>=w||y>=h)return; int p=y*w+x;
    if(clear) {motion[p]=make_float2(0,0);rejected[p]=0;return;}
    float fx=fminf(fmaxf((x+.5f)/grid-.5f,0.f),(float)(gw-1)),fy=fminf(fmaxf((y+.5f)/grid-.5f,0.f),(float)(gh-1));
    int x0=(int)fx,y0=(int)fy,x1=min(x0+1,gw-1),y1=min(y0+1,gh-1); float ax=fx-x0,ay=fy-y0;
    short2 a=((const short2*)(packed+y0*pp))[x0],b=((const short2*)(packed+y0*pp))[x1];
    short2 c=((const short2*)(packed+y1*pp))[x0],d=((const short2*)(packed+y1*pp))[x1];
    float mx=(((float)a.x*(1-ax)+(float)b.x*ax)*(1-ay)+((float)c.x*(1-ax)+(float)d.x*ax)*ay)/32.f;
    float my=(((float)a.y*(1-ax)+(float)b.y*ax)*(1-ay)+((float)c.y*(1-ax)+(float)d.y*ax)*ay)/32.f;
    int cost=confidence ? (grid==1 ? costs[y0*cp+x0] : max(max(costs[y0*cp+x0],costs[y0*cp+x1]),max(costs[y1*cp+x0],costs[y1*cp+x1]))) : 0;
    bool bad=confidence&&(cost>=128||x+mx<0||x+mx>w-1||y+my<0||y+my>h-1);
    motion[p]=bad ? make_float2(0,0) : make_float2(mx,my); rejected[p]=bad;
}
extern "C" __global__ void prepare(const unsigned char* input,int pitch,const float4* previous,float4* current,
    const float2* motion,float* errors,int w,int h,int hasPrevious,int fused) {
    int x=blockIdx.x*blockDim.x+threadIdx.x,y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x>=w||y>=h)return; int p=y*w+x;float3 color=rgb(input+y*pitch+x*4);
    float3 old=make_float3(previous[p].x,previous[p].y,previous[p].z); bool ok=true;
    if(hasPrevious) {float px=x+motion[p].x,py=y+motion[p].y;ok=valid(px,py,w,h);if(ok)old=sample(previous,px,py,w,h,fused);}
    errors[p]=ok ? fmaxf(fabsf(color.x-old.x),fmaxf(fabsf(color.y-old.y),fabsf(color.z-old.z))) : __int_as_float(0x7f800000);
    current[p]=make_float4(color.x,color.y,color.z,0);
}
extern "C" __global__ void scene(const float* errors,unsigned* reset,int w,int h,int first) {
    // Same fixed 16 tiles / 256 samples as the CPU reference. One tiny control
    // value is copied to the host; no image/flow plane leaves GPU memory.
    if(blockIdx.x||blockIdx.y||threadIdx.x||threadIdx.y)return;
    int tiles=0;
    for(int ty=0;ty<4;ty++)for(int tx=0;tx<4;tx++) {
        float total=0;int changed=0;
        for(int y=0;y<4;y++)for(int x=0;x<4;x++) {
            float e=errors[min(h-1,(ty*4+y)*h/16)*w+min(w-1,(tx*4+x)*w/16)];total+=e;if(e>.12f)changed++;
        }
        if(total/16.f>.18f&&changed>=12)tiles++;
    }
    *reset=first||tiles>=12;
}
__device__ unsigned half_guidance(float value) {
    // Match the native host's established half conversion, including tie rules.
    unsigned bits=__float_as_uint(value),sign=(bits>>16)&0x8000,m=bits&0x7fffff;
    int e=(int)((bits>>23)&255)-127+15;
    if(e<=0){if(e< -10)return sign;m=(m|0x800000)>>(1-e);return sign|((m+0x1000)>>13);}
    if(e>=31)return sign|0x7c00;
    return sign|((unsigned)e<<10)|((m+0x1000)>>13);
}
extern "C" __global__ void reliability(float2* motion,unsigned char* rejected,float* errors,unsigned char* halfMotion,int pitch,
    const unsigned* reset,int w,int h,int history) {
    int x=blockIdx.x*blockDim.x+threadIdx.x,y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x>=w||y>=h)return;int p=y*w+x;
    bool cut=*reset!=0;
    if(cut){motion[p]=make_float2(0,0);rejected[p]=0;}
    float2 m=motion[p];
    bool bad=rejected[p]||!isfinite(m.x)||!isfinite(m.y)||(history&&!cut&&(!isfinite(errors[p])||errors[p]>=.12f));
    if(bad){m=make_float2(0,0);motion[p]=m;rejected[p]=1;if(history)errors[p]=__int_as_float(0x7f800000);}
    ((unsigned*)(halfMotion+y*pitch))[x]=half_guidance(m.x)|(half_guidance(m.y)<<16);
}
__device__ float smooth(float low,float high,float value){float t=fminf(fmaxf(__fdiv_rn(value-low,high-low),0.f),1.f);return t*t*(3.f-2.f*t);}
__device__ bool match(const float2* motion,int p,int n){return fabsf(motion[p].x-motion[n].x)<=2&&fabsf(motion[p].y-motion[n].y)<=2;}
__device__ float confidence_at(const float* e,const float2* motion,int x,int y,int w,int h){
    int l=max(0,x-1),r=min(w-1,x+1),above=max(0,y-1)*w,center=y*w,below=min(h-1,y+1)*w,p=center+x;
    if(!match(motion,p,center+l)||!match(motion,p,center+r)||!match(motion,p,above+x)||!match(motion,p,below+x))return 0;
    float a=e[above+l],b=e[above+x],c=e[above+r],d=e[center+l],v=e[p],f=e[center+r],g=e[below+l],hh=e[below+x],i=e[below+r];
    float maximum=fmaxf(fmaxf(fmaxf(a,b),fmaxf(c,d)),fmaxf(fmaxf(v,f),fmaxf(fmaxf(g,hh),i)));
    if(maximum>=.10f)return 0;
    return (1-smooth(.008f,.04f,__fdiv_rn(a+b+c+d+v+f+g+hh+i,9.f)))*(1-smooth(.025f,.10f,maximum));
}
__device__ float blend(float correction,float history,float confidence,float weight,int fused){
    float margin=.02f+confidence*fabsf(history),safe=fminf(fmaxf(history,correction-margin),correction+margin);
    return lerp_exact(correction,safe,weight*confidence,fused);
}
__device__ unsigned char encode(float value){return (unsigned char)__float2int_rn(fminf(fmaxf(value,0.f),1.f)*255.f);}
extern "C" __global__ void accumulate(const unsigned char* input,int ip,unsigned char* output,int op,
    const float4* previous,float4* next,const float2* motion,const float* errors,const unsigned* reset,int w,int h,float weight,int fused){
    int x=blockIdx.x*blockDim.x+threadIdx.x,y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x>=w||y>=h)return;int p=y*w+x;unsigned char* dest=output+y*op+x*4;
    float3 original=rgb(input+y*ip+x*4),raw=rgb(dest),correction=make_float3(raw.x-original.x,raw.y-original.y,raw.z-original.z);
    float confidence=!*reset&&weight>0&&errors[p]<.10f ? confidence_at(errors,motion,x,y,w,h) : 0;
    float px=x+motion[p].x,py=y+motion[p].y;
    if(confidence>0&&valid(px,py,w,h)){
        float3 old=sample(previous,px,py,w,h,fused);
        if(isfinite(old.x)&&isfinite(old.y)&&isfinite(old.z)){
            correction=make_float3(blend(correction.x,old.x,confidence,weight,fused),blend(correction.y,old.y,confidence,weight,fused),blend(correction.z,old.z,confidence,weight,fused));
            dest[0]=encode(original.x+correction.x);dest[1]=encode(original.y+correction.y);dest[2]=encode(original.z+correction.z);
        }
    }
    next[p]=make_float4(correction.x,correction.y,correction.z,0);
}

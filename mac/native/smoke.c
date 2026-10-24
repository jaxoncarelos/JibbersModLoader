#include <assert.h>
#include <stdio.h>
#include <stdint.h>
void *jh_detour_create(void*,void*,void**);
int jh_detour_enable(void*);int jh_detour_disable(void*);void jh_detour_destroy(void*);
__attribute__((noinline,optnone)) static double original(double a,double b,int c) { return a+b+c; }
static double (*trampoline)(double,double,int);
__attribute__((noinline,optnone)) static double replacement(double a,double b,int c) { return trampoline(a,b,c)+100; }
int main(void) {
  double (*volatile call)(double,double,int)=original;
  assert(call(1.5,2.5,3)==7);
  void *h=jh_detour_create((void*)original,(void*)replacement,(void**)&trampoline);
  assert(h && trampoline);
  assert(call(1.5,2.5,3)==7); // Created disabled.
  assert(jh_detour_enable(h)==0); assert(call(1.5,2.5,3)==107);
  assert(jh_detour_disable(h)==0); assert(call(1.5,2.5,3)==7);
  assert(jh_detour_enable(h)==0); assert(call(1.5,2.5,3)==107);
  jh_detour_destroy(h); assert(call(1.5,2.5,3)==7);
  assert(jh_detour_enable(h)!=0);
  puts("Mac native hooks: disabled creation, enable/disable, original, floating point ABI and destroy passed.");
}

#include <stdint.h>
#include <stdatomic.h>
#include <pthread.h>
#include "dobby.h"
#define SLOTS 256
__attribute__((visibility("hidden"))) _Atomic(void*) jh_destinations[SLOTS];
extern void *jh_dispatch_stubs[SLOTS];
static struct { void *target, *replacement, *original; int live; } slots[SLOTS];
static size_t count;
static pthread_mutex_t mutex = PTHREAD_MUTEX_INITIALIZER;
__attribute__((visibility("default"))) void *jh_detour_create(void *target, void *replacement, void **original) {
  if (!target || !replacement || !original) return 0;
  pthread_mutex_lock(&mutex);
  if (count == SLOTS) { pthread_mutex_unlock(&mutex); return 0; }
  // Do not patch an address twice. SDK Around detours must not overlap loader pre-hooks.
  for (size_t j=0;j<count;j++) if (slots[j].target == target) { pthread_mutex_unlock(&mutex); return 0; }
  size_t i=count++;
  slots[i].target=target; slots[i].replacement=replacement;
  void *trampoline=0;
  int rc=DobbyHook(target,jh_dispatch_stubs[i],&trampoline);
  if (rc != 0 || !trampoline) {
    DobbyDestroy(target);
    atomic_store_explicit(&jh_destinations[i],target,memory_order_release);
    pthread_mutex_unlock(&mutex); return 0;
  }
  slots[i].original=trampoline; slots[i].live=1;
  *original=trampoline;
  // The static dispatch stub preserves the complete ABI, including FP/SIMD arguments.
  // A newly created detour is DISABLED, matching the existing C# SDK contract.
  atomic_store_explicit(&jh_destinations[i],trampoline,memory_order_release);
  pthread_mutex_unlock(&mutex);
  return (void*)(i+1);
}
static int change(void *handle,int enabled,int retire) {
  uintptr_t value=(uintptr_t)handle;
  pthread_mutex_lock(&mutex);
  if (!value || value>count || !slots[value-1].live) { pthread_mutex_unlock(&mutex);return 1; }
  size_t i=value-1;
  atomic_store_explicit(&jh_destinations[i],enabled ? slots[i].replacement : slots[i].original,memory_order_release);
  if (retire) slots[i].live=0;
  // Keep disabled dispatch/trampoline storage until process exit: another thread may be
  // executing it. This is bounded at 256 hooks and avoids freeing executable memory in use.
  pthread_mutex_unlock(&mutex);return 0;
}
__attribute__((visibility("default"))) int jh_detour_enable(void *h) { return change(h,1,0); }
__attribute__((visibility("default"))) int jh_detour_disable(void *h) { return change(h,0,0); }
__attribute__((visibility("default"))) void jh_detour_destroy(void *h) { change(h,0,1); }

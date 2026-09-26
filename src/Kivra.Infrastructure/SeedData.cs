using Kivra.Domain;
using Microsoft.EntityFrameworkCore;
namespace Kivra.Infrastructure;
public static class SeedData {
 public static readonly string[] DefaultCategoryNames={"Prepared Food","Cut Vegetables","Raw Meat","Seafood","Sauces","Dry Stock","Dairy","Frozen Items","Gravy/Base","Other"};

 public static async Task EnsureDefaultCategoriesAsync(KivraDbContext db,CancellationToken ct=default) {
  var existing=await db.Categories.ToListAsync(ct);
  foreach(var name in DefaultCategoryNames) {
   var category=existing.FirstOrDefault(x=>string.Equals(x.Name.Trim(),name,StringComparison.OrdinalIgnoreCase));
   if(category is null) {
    category=new Category{Name=name};
    db.Categories.Add(category);
    existing.Add(category);
   } else {
    category.Name=name;
    category.Active=true;
    category.UpdatedAt=DateTimeOffset.UtcNow;
   }
  }
  await db.SaveChangesAsync(ct);
 }

 public static async Task EnsureSeededAsync(KivraDbContext db,string adminPin,CancellationToken ct=default) {
  await EnsureDefaultCategoriesAsync(db,ct);
  var locationNames=new[]{"Chiller 1","Chiller 2","Chiller 3","Freezer 1","Freezer 2","Freezer 3","Dry Store","Preparation Area","Sauce Station"};
  var existingLocations=await db.StorageLocations.ToListAsync(ct);
  foreach(var name in locationNames.Where(n=>existingLocations.All(x=>x.Name!=n)))db.StorageLocations.Add(new StorageLocation{Name=name});
  if(!await db.Printers.AnyAsync(ct))db.Printers.Add(new Printer{Name="Development Fake Printer",Model="Simulated TSPL",Driver="Fake"});
  if(!await db.Users.AnyAsync(ct))db.Users.Add(new User{DisplayName="Administrator",PinHash=PinAuthentication.Hash(adminPin),Role=UserRole.Administrator});
  await db.SaveChangesAsync(ct);
  var categories=await db.Categories.ToDictionaryAsync(x=>x.Name,ct);var locations=await db.StorageLocations.ToDictionaryAsync(x=>x.Name,ct);
  var definitions=new[]{
   new ItemSeed("Chicken Pakora","Prepared Food",Classification.NonVeg,"PREPARED",2,ShelfLifeUnit.Days,"Chiller 1"),
   new ItemSeed("Chicken Lollipop","Prepared Food",Classification.NonVeg,"PREPARED",2,ShelfLifeUnit.Days,"Chiller 1"),
   new ItemSeed("Cut Onion","Cut Vegetables",Classification.Veg,"CUT ON",1,ShelfLifeUnit.Days,"Chiller 1"),
   new ItemSeed("Cut Capsicum","Cut Vegetables",Classification.Veg,"CUT ON",1,ShelfLifeUnit.Days,"Chiller 1"),
   new ItemSeed("Cut Carrot","Cut Vegetables",Classification.Veg,"CUT ON",2,ShelfLifeUnit.Days,"Chiller 1"),
   new ItemSeed("Raw Chicken","Raw Meat",Classification.NonVeg,"RECEIVED",2,ShelfLifeUnit.Days,"Chiller 2"),
   new ItemSeed("Raw Prawns","Seafood",Classification.NonVeg,"THAWED ON",1,ShelfLifeUnit.Days,"Chiller 2"),
   new ItemSeed("Mayonnaise","Sauces",Classification.Veg,"OPENED",30,ShelfLifeUnit.Days,"Chiller 3"),
   new ItemSeed("Schezwan Sauce","Sauces",Classification.Veg,"OPENED",14,ShelfLifeUnit.Days,"Sauce Station"),
   new ItemSeed("Maida","Dry Stock",Classification.Veg,"OPENED",30,ShelfLifeUnit.Days,"Dry Store")};
  var existingItems=await db.Items.Select(x=>x.Name).ToListAsync(ct);
  foreach(var x in definitions.Where(x=>!existingItems.Contains(x.Name)))db.Items.Add(new Item{Name=x.Name,CategoryId=categories[x.Category].Id,Classification=x.Classification,LabelType="Food",DateTerminology=x.Terminology,ShelfLifeValue=x.Value,ShelfLifeUnit=x.Unit,DefaultStorageLocationId=locations[x.Storage].Id});
  await db.SaveChangesAsync(ct);
 }
 sealed record ItemSeed(string Name,string Category,Classification Classification,string Terminology,int Value,ShelfLifeUnit Unit,string Storage);
}

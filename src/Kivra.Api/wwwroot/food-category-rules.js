// Ordered from specific raw ingredients to broader prepared-food terms.
// Each entry maps common Indian and Indo-Chinese kitchen vocabulary to an
// existing KIVRA category. The UI only applies a rule when that category exists.
const foodCategoryRules=[
 {categories:['Seafood'],terms:[
  'prawn','prawns','shrimp','fish','salmon','tuna','surmai','rawas','rohu','katla','hilsa','pomfret','bangda','mackerel','kingfish','basa','tilapia','sole fish','crab','lobster','squid','calamari','octopus','mussel','oyster','clam','anchovy','sardine'
 ]},
 {categories:['Raw Meat','Meat'],terms:[
  'raw chicken','chicken breast','chicken thigh','chicken leg','chicken wing','chicken mince','mutton','goat meat','lamb','beef','buff','pork','bacon','ham','sausage','keema','kheema','mince','boneless','tenderloin','steak','duck','turkey','liver','kidney','offal'
 ]},
 {categories:['Cut Vegetables','Vegetables'],terms:[
  'curry leaf','curry leaves','kadi patta','kadipatta','onion','pyaz','capsicum','bell pepper','shimla mirch','carrot','gajar','tomato','tamatar','potato','aloo','cucumber','kheera','lettuce','cabbage','patta gobi','cauliflower','phool gobi','broccoli','beetroot','radish','mooli','turnip','shalgam','pumpkin','kaddu','bottle gourd','lauki','ridge gourd','turai','bitter gourd','karela','okra','bhindi','brinjal','eggplant','baingan','zucchini','mushroom','sweet corn','baby corn','green pea','matar','french bean','spring onion','scallion','coriander','dhaniya','mint','pudina','spinach','palak','methi','fenugreek leaf','celery','parsley','basil','lemon','lime','ginger','adrak','garlic','lahsun','green chilli','red chilli fresh','chilli','sprout','tofu','bean sprout','bamboo shoot','water chestnut'
 ]},
 {categories:['Dairy'],terms:[
  'milk','full cream milk','toned milk','cream','fresh cream','whipping cream','cheese','mozzarella','cheddar','parmesan','cream cheese','paneer','butter','ghee','yogurt','yoghurt','curd','dahi','khoya','khoa','mawa','malai','condensed milk','milk powder','buttermilk','chaas'
 ]},
 {categories:['Sauces','Sauce'],terms:[
  'bbq','barbecue','sauce','chutney','mayo','mayonnaise','dip','dressing','ketchup','mustard sauce','relish','salsa','pesto','vinegar','soy sauce','soya sauce','dark soy','light soy','schezwan','szechuan','sichuan sauce','chilli sauce','chili sauce','hot sauce','sriracha','tabasco','oyster sauce','fish sauce','hoisin','teriyaki','worcestershire','thousand island','tartar','peri peri sauce','mint chutney','green chutney','tamarind chutney','imli chutney','garlic sauce','black bean sauce','plum sauce','sweet chilli','chilli oil'
 ]},
 {categories:['Dry Stock','Dry'],terms:[
  'rawa','rava','sooji','suji','semolina','kaju','cashew','magaj','melon seed','til','sesame','ajinamoto','ajinomoto','msg','monosodium glutamate','flour','maida','atta','wheat flour','besan','gram flour','cornflour','corn flour','rice flour','rice','basmati','brown rice','jasmine rice','poha','flattened rice','sabudana','sago','oats','quinoa','barley','jowar','bajra','ragi','millet','makki atta','sugar','brown sugar','icing sugar','jaggery','gur','salt','rock salt','sendha namak','black salt','kala namak','pepper','black pepper','white pepper','spice','masala','garam masala','chaat masala','turmeric','haldi','jeera','cumin','rai','mustard seed','saunf','fennel','elaichi','cardamom','dalchini','cinnamon','clove','laung','nutmeg','jaiphal','mace','javitri','star anise','chakra phool','bay leaf','tej patta','kasuri methi','hing','asafoetida','coriander seed','dhaniya powder','chilli powder','paprika','oregano','thyme','rosemary','five spice','schezwan pepper','sichuan pepper','lentil','dal','toor dal','arhar dal','moong dal','masoor dal','urad dal','chana dal','chana','rajma','chickpea','kabuli chana','bean','lobia','pasta','macaroni','spaghetti','noodle','noodles','hakka noodle','chow mein noodle','rice noodle','glass noodle','vermicelli','sevai','almond','badam','walnut','akhrot','pista','pistachio','peanut','groundnut','moongfali','raisin','kishmish','sunflower seed','pumpkin seed','chia seed','flax seed','alsi','poppy seed','khus khus','papad','breadcrumb','bread crumb','panko','baking powder','baking soda','yeast','cocoa','chocolate chip','coffee','tea','green tea','corn starch','tapioca starch','potato starch','custard powder','gelatin','agar agar','food colour','food color','vanilla essence','kewra water','rose water','coconut powder','desiccated coconut','dry coconut'
 ]},
 {categories:['Frozen Items','Frozen'],terms:[
  'frozen','ice cream','french fries','fries','hash brown','frozen pea','frozen corn','frozen paratha','frozen naan','frozen momo','frozen spring roll','frozen samosa','frozen kebab','frozen patty','frozen nugget','frozen seafood','frozen meat','frozen vegetable'
 ]},
 {categories:['Gravy/Base','Gravy','Base'],terms:[
  'gravy','curry base','makhani base','brown base','white base','onion tomato base','stock','broth','jus','demi glace','marinade','marination','slurry','roux','puree','tomato puree','cashew paste','ginger garlic paste','chilli paste','schezwan paste','tandoori paste','thai curry paste','manchurian gravy','hot garlic gravy','sweet and sour gravy','chilli gravy'
 ]},
 {categories:['Prepared Food','Prepared'],terms:[
  'cooked','fried','roasted','grilled','boiled','baked','steamed','sauteed','pakora','pakoda','bhaji','samosa','tikka','kebab','kabab','biryani','pulao','fried rice','jeera rice','khichdi','chow mein','hakka noodles','manchurian','spring roll','momo','dumpling','soup','salad','raita','dessert','cake','pastry','pudding','halwa','kheer','gulab jamun','rasgulla','jalebi','chicken 65','chilli chicken','chili chicken','gobi 65','paneer tikka','dal fry','dal tadka','cooked rice','cooked dal','boiled pasta','boiled noodle'
 ]}
];
